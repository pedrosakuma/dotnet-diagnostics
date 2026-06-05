using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnosticsMcp.Core;
using DotnetDiagnosticsMcp.Core.Activities;
using DotnetDiagnosticsMcp.Core.Bytes;
using DotnetDiagnosticsMcp.Core.Capabilities;
using DotnetDiagnosticsMcp.Core.Collection;
using DotnetDiagnosticsMcp.Core.Contention;
using DotnetDiagnosticsMcp.Core.Container;
using DotnetDiagnosticsMcp.Core.Counters;
using DotnetDiagnosticsMcp.Core.CpuSampling;
using DotnetDiagnosticsMcp.Core.Db;
using DotnetDiagnosticsMcp.Core.Dump;
using DotnetDiagnosticsMcp.Core.EventSources;
using DotnetDiagnosticsMcp.Core.Exceptions;
using DotnetDiagnosticsMcp.Core.Gc;
using DotnetDiagnosticsMcp.Core.Logs;
using DotnetDiagnosticsMcp.Core.Drilldown;
using DotnetDiagnosticsMcp.Core.Investigation;
using DotnetDiagnosticsMcp.Core.Jit;
using DotnetDiagnosticsMcp.Core.JitCapture;
using DotnetDiagnosticsMcp.Core.Memory;
using DotnetDiagnosticsMcp.Core.NativeAlloc;
using DotnetDiagnosticsMcp.Core.OffCpu;
using DotnetDiagnosticsMcp.Core.ProcessDiscovery;
using DotnetDiagnosticsMcp.Core.Security;
using DotnetDiagnosticsMcp.Core.ThreadPool;
using DotnetDiagnosticsMcp.Core.Threads;
using DotnetDiagnosticsMcp.Core.Triage;
using DotnetDiagnosticsMcp.Core.UseCases;
using DotnetDiagnosticsMcp.Server.Security;
using DotnetDiagnosticsMcp.Server.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static DotnetDiagnosticsMcp.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnosticsMcp.Server.Tools;

/// <summary>
/// MCP tools that expose the dotnet-diagnostics-mcp Core diagnostic primitives.
/// Every tool returns a <see cref="DiagnosticResult{T}"/> envelope carrying a short summary,
/// next-action hints, and the typed payload — so a low-context LLM can drill down without
/// re-reading the server instructions on every turn.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    private static readonly string[] HostingCounterProviders = ["Microsoft.AspNetCore.Hosting"];

    [RequireScope("read-counters")]
    [Description(
        "Lists all .NET processes on the local machine that expose a Diagnostic IPC endpoint. " +
        "Returns process id, runtime version, OS, architecture and the managed entrypoint assembly. " +
        "Usually the first tool to call in any investigation.")]
    public static DiagnosticResult<IReadOnlyList<DotnetProcess>> ListDotnetProcesses(IProcessDiscovery discovery)
        => ProcessInspectionUseCases.ListProcesses(discovery);

    [RequireScope("read-counters")]
    [Description(
        "Returns metadata for a single .NET process identified by its OS process id, " +
        "or an error result if the process is not running or does not expose a diagnostic endpoint. " +
        "processId is optional: when exactly one .NET process is reachable on the host the server " +
        "auto-resolves it and stamps a compact capability digest on the response envelope.")]
    public static Task<DiagnosticResult<DotnetProcess>> GetProcessInfo(
        IProcessDiscovery discovery,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
        => ProcessInspectionUseCases.GetProcessInfoAsync(discovery, resolver, processId, cancellationToken);

    [RequireScope("read-counters")]
    [Description(
        "Probes the target process to determine which diagnostic tools the server can use against it. " +
        "Detects CoreCLR vs NativeAOT (NativeAOT lacks CPU sampling and gcdump) and returns a capability matrix. " +
        "Takes up to ~2 seconds while probing the SampleProfiler provider. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-resolves it. " +
        "Most callers no longer need this tool first — every other tool already attaches a compact capability digest " +
        "on its response envelope, so call this explicitly only when you need the full matrix.")]
    public static Task<DiagnosticResult<DiagnosticCapabilities>> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
        => ProcessInspectionUseCases.GetCapabilitiesAsync(detector, resolver, processId, cancellationToken);

    [RequireScope("read-counters")]
    [Description(
        "Reads Linux cgroup v2 files for the target process: cpu.stat (throttling), cpu.max (quota), " +
        "memory.current / memory.max / memory.events (OOM kills), cpu/memory/io.pressure (PSI), pids and " +
        "oom_score. Closes the #1 K8s blind spot — 'app is slow but runtime CPU counters look fine' is usually " +
        "CPU throttling at the cgroup level, completely invisible from the runtime. " +
        "Cheap (file reads only, no privilege, no EventPipe session). Returns partial signals + Notes on " +
        "non-Linux hosts, cgroup v1 hosts and old kernels lacking PSI. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-resolves it.")]
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
            return new[]
            {
                new NextActionHint("collect_sample",
                    $"CPU throttling > 5% ({cpu.ThrottlePercent:F1}% of periods). Sample on-CPU stacks to see which code is hitting the quota.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 10 }),
            };
        }
        if (s.Memory is { UsageFraction: > 0.85 } mem)
        {
            return new[]
            {
                new NextActionHint("inspect_heap",
                    $"Memory at {(mem.UsageFraction ?? 0) * 100:F0}% of limit. Inspect the live heap to identify the dominant retainers before the cgroup OOM-kills.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["topTypes"] = 25 }),
            };
        }
        if (!s.InContainer)
        {
            return new[]
            {
                new NextActionHint("collect_events",
                    "Not in a container envelope — runtime EventCounters remain the cheapest first signal.",
                    new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
            };
        }
        return new[]
        {
            new NextActionHint("collect_events",
                "No kernel-level pressure detected. Move up the stack to runtime counters.",
                new Dictionary<string, object?> { ["processId"] = s.ProcessId, ["durationSeconds"] = 5 }),
        };
    }

    [RequireScope("read-counters")]
    [Description(
        "Samples OS-level memory metrics (RSS, PSS, anonymous/private pages, page faults) " +
        "at regular intervals over a configurable window, then computes per-second deltas and a " +
        "growth verdict ('growing', 'stable', 'shrinking'). Works on any OS process — CoreCLR, " +
        "NativeAOT, or non-.NET — no EventPipe session required. " +
        "On Linux reads /proc/<pid>/smaps_rollup (Rss, Pss, Anonymous) with an automatic " +
        "fallback to /proc/<pid>/smaps accumulation on kernels < 4.14, and /proc/<pid>/stat " +
        "(minflt/majflt) for page-fault counters. On Windows calls GetProcessMemoryInfo " +
        "(WorkingSetSize, PrivateUsage, PageFaultCount). " +
        "Use this as a lightweight memory-leak signal before reaching for heap dumps — it answers " +
        "'is the process growing and how fast?' without walking the heap. " +
        "When processId is provided it is used directly as the OS pid (no .NET IPC check). " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
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
            // Explicit OS pid: bypass the .NET diagnostic IPC resolver so the tool works on
            // any process — NativeAOT, non-.NET, or any pid not visible to the IPC socket.
            pid = processId.Value;
        }
        else if (processId is < 0)
        {
            return InvalidArg<MemoryTrend>(nameof(processId), "must be a positive process id");
        }
        else
        {
            // null or 0 → auto-resolve via the .NET diagnostic IPC (finds the lone .NET process).
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

    [RequireScope("read-counters")]
    [Description(
        "Reads a managed process's runtime configuration: best-effort GC and ThreadPool settings from a short ClrMD attach, tiered-compilation overrides from filtered runtime env vars, and a forward-compatible appContextSwitches field that currently stays empty with an explanatory note. " +
        "Environment variables are filtered to the DOTNET_ / COMPlus_ / ASPNETCORE_ / DOTNET_SYSTEM_ prefixes as a hard security boundary. " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
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

    [RequireScope("read-counters")]
    [Description(
        "Phase 12 IoT-style triage: collects counters (5s), runs server-side classification, and returns a verdict with actionable hints. " +
        "The LLM just follows the first hint — no interpretation needed. " +
        "Verdicts: cpu-bound, gc-pressure, memory-pressure, threadpool-starvation, lock-contention, io-bound, healthy. " +
        "Severity: critical (immediate action), degraded (investigate), healthy (all clear).")]
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

        // Collect counters with default providers (System.Runtime, ASP.NET Core, Kestrel).
        var snapshot = await collector.CollectAsync(
            pid,
            TimeSpan.FromSeconds(durationSeconds),
            providers: null, // defaults
            meters: ["Microsoft.AspNetCore.Hosting"], // for request duration p95
            intervalSeconds: 1,
            maxInstrumentTimeSeries: 100,
            cancellationToken).ConfigureAwait(false);

        // Extract request duration p95 if available.
        var requestDuration = HeadlineCounters.FindRequestDuration(snapshot.Meters);
        var requestDurationP95 = requestDuration?.Histogram?.P95;

        // Classify using the shared logic.
        var triage = TriageClassifier.Classify(snapshot, requestDurationP95);

        // Build hints based on verdict.
        var hints = BuildTriageHints(triage, pid);

        // Build summary with top indicators.
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

    private static List<NextActionHint> BuildTriageHints(TriageResult triage, int pid)
    {
        var hints = new List<NextActionHint>();

        // Add hints based on verdict (same logic as auto-hints in SnapshotCounters).
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
                // Lead with the allocation profiler when the driver is a high allocation rate (mirrors
                // the snapshot-counter auto-hint), since kind="gc" captures pauses, not allocation sites.
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

            default: // Healthy
                hints.Add(new NextActionHint("collect_events", "System looks healthy — confirm with GC events if response times are high.",
                    new Dictionary<string, object?> { ["processId"] = pid, ["kind"] = "gc", ["durationSeconds"] = 10 }));
                break;
        }

        // Add hints for secondary verdicts (less detailed).
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

    [RequireScope("read-counters")]
    [Description(
        "Inspects OS-level FD / handle / socket state for a process. On Linux it reads /proc/<pid>/fd, /proc/<pid>/net/tcp{,6} and /proc/<pid>/limits to classify descriptors, aggregate TCP states and compute the nofile usage fraction. " +
        "On Windows it queries GetProcessHandleCount and reports a note that per-handle and per-socket breakdown is not yet implemented. " +
        "durationSeconds=0 returns a single snapshot (default); durationSeconds >= 2 samples a short trend window. " +
        "When processId is provided it is used directly as the OS pid (no .NET IPC check). " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
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
        if (snapshot.Requests.Count == 0)
        {
            return $"Process {snapshot.ProcessId}: no in-flight ASP.NET Core requests observed during the last {windowSeconds}s.";
        }

        var preview = string.Join(", ", snapshot.Requests.Take(3).Select(request =>
            $"{request.Method} {request.Endpoint} ({request.StartedAtMs:F0} ms, tid {request.ThreadId})"));
        return $"Process {snapshot.ProcessId}: {snapshot.Requests.Count} in-flight ASP.NET Core request(s) observed during the last {windowSeconds}s: {preview}{(snapshot.Requests.Count > 3 ? ", …" : string.Empty)}.";
    }

    private static NextActionHint[] BuildRequestsNowHints(RequestsNowSnapshot snapshot)
    {
        if (snapshot.Requests.Count == 0)
        {
            return
            [
                new NextActionHint(
                    "collect_events",
                    "No hanging requests were active in this 2s window — re-run during the incident or cross-check Hosting counters.",
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

    [RequireScope("read-counters")]
    [Description(
        "Collects EventCounters from the target process over a fixed time window and returns the " +
        "latest value seen per counter. Default providers cover the .NET runtime, ASP.NET Core hosting " +
        "and Kestrel; pass a custom list to observe other EventSources. Cheapest first signal — always run " +
        "before sampling or dumps.")]
    public static async Task<DiagnosticResult<CounterSnapshot>> SnapshotCounters(
        ICounterCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 5.")] int durationSeconds = 5,
        [Description("Optional list of EventCounter provider names to subscribe to. If null, defaults to System.Runtime, Microsoft.AspNetCore.Hosting and Microsoft-AspNetCore-Server-Kestrel. Pass an empty list to skip legacy EventCounters.")]
        string[]? providers = null,
        [Description("Optional list of Meter names to subscribe to through System.Diagnostics.Metrics. Null/empty disables Meter collection.")]
        string[]? meters = null,
        [Description("Refresh interval (in seconds) requested from each provider. Defaults to 1.")] int intervalSeconds = 1,
        [Description("kind=counters only. Maximum Meter time series (and histograms) retained before the collector caps results. Defaults to 1000.")]
        int maxInstrumentTimeSeries = 1000,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns ~12 headline counters plus http.server.request.duration p95 when available. 'detail' returns the full counter + meter list. 'raw' is equivalent to detail for this tool. The complete snapshot is always retained behind the issued handle — drill in with query_snapshot(handle, view=summary).")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.SnapshotCounters(
            collector, resolver, handles,
            processId, durationSeconds, providers, meters, intervalSeconds, maxInstrumentTimeSeries, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures a CPU sample from the target process and returns the top-N hotspots aggregated by method. " +
        "On CoreCLR uses EventPipe SampleProfiler (managed frames with mvid+token handoff). " +
        "Optionally, resolveMethodInstantiations=true performs a second ClrMD attach after sampling to recover closed generic method signatures for the hottest managed frames; on Linux that requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target while the attach runs. " +
        "On NativeAOT (Linux) falls back to 'perf record' when available — frames are native symbols only, MethodIdentity is null. " +
        "Each hotspot reports both inclusive and exclusive sample counts. Run after snapshot_counters shows elevated cpu-usage. " +
        "Spec-compliant clients can call this tool as an MCP Task (tools/call with params.task) and poll via tasks/get + tasks/result, " +
        "or rely on MCP-native notifications/progress + notifications/cancelled on the same tools/call request.")]
    public static async Task<DiagnosticResult<CpuSample>> CollectCpuSample(
        ICpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of hotspots to return. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("If true, attempts to resolve top hotspots to file:line via PDB / SourceLink and stamps the resolved SourceLocation onto each MethodIdentity payload (issue #28 — makes dotnet-assembly-mcp.get_method_source optional when PDBs are reachable). Defaults to true; set to false to skip PDB I/O when symbols are known to be unreachable.")] bool resolveSourceLines = true,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to the symbol reader (e.g. '/symbols' or 'srv*c:\\symcache*https://msdl.microsoft.com/download/symbols'). Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule/module directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on the `Diagnostics:SymbolServerAllowlist` allowlist or the call is rejected with a `SymbolServerNotAllowed` envelope. Local file paths always pass through. Ignored when resolveSourceLines=false.")] string? symbolPath = null,
        [Description("Cap on how many top hotspots get source-resolved. Must be >= 1. Defaults to the requested topN so every emitted MethodIdentity carries its resolved SourceLocation when available.")] int? maxResolvedSources = null,
        [Description("If true, performs an opt-in ClrMD attach after sampling to recover closed generic instantiations for the hottest managed frames (displayed on MethodIdentity as ClosedSignature + GenericTypeArguments.Method). CoreCLR only. On Linux this requires CAP_SYS_PTRACE (or ptrace_scope=0) and briefly suspends the target during the attach. Defaults to false to keep the EventPipe-only path lightweight.")] bool resolveMethodInstantiations = false,
        [Description("Cap on how many top hotspots get ClrMD generic-instantiation enrichment. Must be >= 1. Defaults to the requested topN so the enrichment work stays bounded to the hottest frames.")] int? maxResolvedMethodInstantiations = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 hotspots inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full sample is always retained behind the issued handle — drill in with get_call_tree.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<CpuSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<CpuSample>(nameof(topN), "must be >= 1");
        var effectiveMaxResolved = maxResolvedSources ?? topN;
        if (effectiveMaxResolved < 1) return InvalidArg<CpuSample>(nameof(maxResolvedSources), "must be >= 1");
        var effectiveMaxResolvedInstantiations = maxResolvedMethodInstantiations ?? topN;
        if (effectiveMaxResolvedInstantiations < 1) return InvalidArg<CpuSample>(nameof(maxResolvedMethodInstantiations), "must be >= 1");

        // B4 / issue #165 / M3: caller-supplied srv*http(s):// symbol paths are an SSRF
        // surface. Default deny; operators allowlist hosts via Diagnostics:SymbolServerAllowlist.
        if (resolveSourceLines)
        {
            var symbolDenial = ValidateSymbolPath<CpuSample>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
            if (symbolDenial is not null) return symbolDenial;
        }

        var resolved = await ResolveContextAsync<CpuSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        var srcOpts = resolveSourceLines
            ? new SourceResolutionOptions(Enabled: true, SymbolPath: symbolPath, MaxResolved: effectiveMaxResolved)
            : null;
        var instantiationOpts = resolveMethodInstantiations
            ? new MethodInstantiationResolutionOptions(Enabled: true, MaxResolved: effectiveMaxResolvedInstantiations)
            : null;

        CpuSampleResult result;
        try
        {
            result = await CollectionProgressTicker.RunAsync(
                requestContext,
                "collect_cpu_sample",
                TimeSpan.FromSeconds(durationSeconds),
                TimeSpan.FromSeconds(1),
                ct => sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, srcOpts, instantiationOpts, ct),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // MCP-native cancel path (notifications/cancelled). Return a partial envelope so the
            // client knows the operation terminated cleanly.
            return WithContext(
                new DiagnosticResult<CpuSample>(
                    $"CPU sampling cancelled by the client after starting against pid {pid}. " +
                    "No samples were retained — restart the collection to capture data.",
                    Array.Empty<NextActionHint>())
                {
                    Cancelled = true,
                },
                ctx);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("elevation", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("privilege", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase) ||
                                                    ex.Message.Contains("NativeAOT", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<CpuSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (Exception ex) when (resolveMethodInstantiations && ex is not OperationCanceledException)
        {
            return WithContext(ClassifyAttachFailure<CpuSample>("collect_cpu_sample", pid, ex), ctx);
        }

        var handle = handles.Register(pid, "cpu-sample", result.Artifact, CpuSampleHandleTtl);

        var ok = BuildCpuSampleResult(
            result.Summary,
            durationSeconds,
            handle.Id,
            handle.ExpiresAt,
            depth,
            new NextActionHint("query_snapshot", "Walk the merged caller→callee tree built from the same samples.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("collect_events", "Confirm hot path isn't driven by exception-heavy control flow.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, ctx);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures allocation samples from the target process and returns the top-N types by total allocated bytes " +
        "and by event count. Uses GCAllocationTick events from Microsoft-Windows-DotNETRuntime (GCKeyword=0x1, Verbose), " +
        "which fire roughly every 100 KB of total managed allocations. " +
        "On CoreCLR, TypeName is fully populated with managed type names. " +
        "On NativeAOT, GCAllocationTick events fire but TypeName is empty — all events roll up under '<unknown>' " +
        "and only the total event count and bytes are meaningful; use collect_cpu_sample for per-site attribution on AOT. " +
        "Returns two ranked lists (TopByBytes, TopByCount) and a handle for call-site drill-down via get_call_tree. " +
        "When managed symbols are available, get_call_tree projects MethodIdentity (MVID + token) onto the returned frames for dotnet-assembly-mcp handoff. " +
        "Run after snapshot_counters shows elevated GC pressure or growing gen0/gen1 heap sizes.")]
    public static async Task<DiagnosticResult<AllocationSample>> CollectAllocationSample(
        EventPipeAllocationSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of types to return in each top-N list (TopByBytes and TopByCount). Must be >= 1. Defaults to 25.")] int topN = 25,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<AllocationSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<AllocationSample>(nameof(topN), "must be >= 1");

        var resolved = await ResolveContextAsync<AllocationSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;
        var ctx = resolved.Context;

        AllocationSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<AllocationSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process", "Check capability matrix to confirm what's available for this process.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var sample = result.Summary;
        var handle = handles.Register(pid, "allocation-sample", new AllocationSampleArtifact(sample, result.Artifact), CpuSampleHandleTtl);

        var topType = sample.TopByBytes.Count > 0 ? sample.TopByBytes[0] : null;
        var unknownOnly = topType?.TypeName == "<unknown>" && sample.TopByBytes.Count == 1;
        var summaryText = unknownOnly
            ? $"Captured {sample.TotalEvents} allocation events ({sample.TotalBytes:N0} bytes total) over {durationSeconds}s, " +
              $"but TypeName was empty for all events (expected on NativeAOT). " +
              $"Drill into allocation call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\") to see native allocation frames."
            : topType is not null
                ? $"Captured {sample.TotalEvents} allocation events ({sample.TotalBytes:N0} bytes total) over {durationSeconds}s. " +
                  $"Top type by bytes: {topType.TypeName} ({topType.TotalBytes:N0} bytes, {topType.EventCount} events). " +
                  $"Drill into allocation call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\")."
                : $"Captured {sample.TotalEvents} allocation events but no type aggregation surfaced — " +
                  $"increase durationSeconds or drive a workload that allocates during the window.";

        var ok = DiagnosticResult.OkWithHandle(
            sample,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Walk the merged allocation call-site tree to find which code paths are allocating the most.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("collect_sample", "Cross-reference: identify hot CPU paths that correlate with the top allocating types.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }),
            new NextActionHint("collect_events", "Observe GC pause frequency and generation distribution caused by this allocation load.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds }));
        return WithContext(ok, ctx);
    }

    [RequireScope("investigation-export")]
    [Description(
        "Returns a pruned caller→callee tree from a prior collect_cpu_sample or collect_allocation_sample run, " +
        "addressed by its handle. Frames are enriched with MethodIdentity (MVID + metadata token) when the producer captured one. " +
        "Use `rootMethodFilter` to anchor the walk at a method substring (case-insensitive). " +
        "`maxDepth` and `maxNodes` bound the response size so the LLM stays under its token budget. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<CallTreeView> GetCallTree(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_cpu_sample call.")] string handle,
        [Description("Optional case-insensitive substring; the tree is re-rooted at the highest-ranked frame whose method name contains this text.")] string? rootMethodFilter = null,
        [Description("Maximum tree depth from the root. Must be >= 1. Defaults to 8.")] int maxDepth = 8,
        [Description("Approximate cap on the number of nodes returned (top children at each level). Must be >= 1. Defaults to 200.")] int maxNodes = 200)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CallTreeView>(nameof(handle), "is required");
        if (maxDepth < 1) return InvalidArg<CallTreeView>(nameof(maxDepth), "must be >= 1");
        if (maxNodes < 1) return InvalidArg<CallTreeView>(nameof(maxNodes), "must be >= 1");

        var artifact = ResolveTraceArtifact(handles, handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<CallTreeView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        // The call-tree rendering is host-neutral (CallTreeNode / CallTreeView / projection are Core
        // types), so it lives in CpuSampleQueryDispatcher and is shared with the CLI `session` REPL
        // (#300). The handle / maxDepth / maxNodes preamble above is unchanged.
        return CpuSampleQueryDispatcher.RenderCallTree(artifact, handle, rootMethodFilter, maxDepth, maxNodes);
    }

    internal static CpuSampleTraceArtifact? ResolveTraceArtifact(IDiagnosticHandleStore handles, string handle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(handle);
        return handles.TryGet<CpuSampleTraceArtifact>(handle)
            ?? handles.TryGet<AllocationSampleArtifact>(handle)?.TraceArtifact;
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures off-CPU stacks for the target process — where threads are blocked, for how long, and on which " +
        "kernel/user frame. Companion to collect_cpu_sample: on-CPU sampling shows hot code, off-CPU shows time " +
        "spent waiting (futex, IO, sleep, lock). Closes the 'latency high, CPU low' diagnostic gap that on-CPU " +
        "samples can't see by definition. " +
        "Backend: Linux only in this release — runs 'perf record -a -e sched:sched_switch --call-graph dwarf' " +
        "for durationSeconds. Requires the perf binary in PATH and CAP_PERFMON (or perf_event_paranoid <= -1). " +
        "Windows ETW kernel CSwitch support tracked in issue #41 sub-slice 2b. " +
        "Returns the top-N blocking stacks inline and a handle for query_off_cpu_snapshot drilldown.")]
    public static async Task<DiagnosticResult<OffCpuSnapshot>> CollectOffCpuSample(
        IOffCpuSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Sampling window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of blocking stacks returned inline (the full set lives behind the handle). Defaults to 25.")] int topN = 25,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 blocking stacks inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full artifact is always retained behind the issued handle — drill in with query_off_cpu_snapshot.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<OffCpuSnapshot>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<OffCpuSnapshot>(nameof(topN), "must be >= 1");

        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = ValidateSymbolPath<OffCpuSnapshot>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

        var resolved = await ResolveContextAsync<OffCpuSnapshot>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        OffCpuSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, symbolPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Confirm which signals are available on this host before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (UnauthorizedAccessException ex)
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                $"collect_off_cpu_sample could not start NT Kernel Logger capture for pid {pid}: Windows denied access to the ContextSwitch provider.",
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "After granting either BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance') to the sidecar account and restarting the Windows service, re-check capabilities before retrying.",
                    new Dictionary<string, object?> { ["processId"] = pid }),
                new NextActionHint("collect_sample",
                    "Retry after the sidecar account has one of the two supported Windows paths: BUILTIN\\Administrators membership or SeSystemProfilePrivilege ('Profile system performance').",
                    new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = durationSeconds, ["topN"] = topN }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("CAP_", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<OffCpuSnapshot>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Check capability matrix; install linux-perf and add CAP_PERFMON to the sidecar securityContext.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var summary = result.Summary;
        var handle = handles.Register(pid, OffCpuHandleKind, result.Artifact, CpuSampleHandleTtl);

        var inlineSummary = summary;
        var droppedStacks = 0;
        if (depth == SamplingDepth.Summary && summary.TopBlockingStacks.Count > 3)
        {
            droppedStacks = summary.TopBlockingStacks.Count - 3;
            inlineSummary = summary with { TopBlockingStacks = summary.TopBlockingStacks.Take(3).ToArray() };
        }

        var topStack = summary.TopBlockingStacks.Count > 0 ? summary.TopBlockingStacks[0] : null;
        var summaryText = topStack is not null
            ? (depth == SamplingDepth.Summary && droppedStacks > 0
                ? $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s — showing top {inlineSummary.TopBlockingStacks.Count} of {summary.TopBlockingStacks.Count} blocking stack(s) (dropped {droppedStacks}; handle has all). " +
                  $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
                  $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
                  $"Drill with query_snapshot(handle=\"{handle.Id}\")."
                : $"Captured {summary.SchedSwitches} switches across {summary.DistinctThreads} threads over {durationSeconds}s. " +
                  $"Total off-CPU: {summary.TotalOffCpuMicros / 1000.0:F1} ms. " +
                  $"Top blocker: {topStack.LeafFrame} ({topStack.OffCpuMicros / 1000.0:F1} ms, state={topStack.DominantState}). " +
                  $"Drill with query_snapshot(handle=\"{handle.Id}\").")
            : $"Captured {summary.SchedSwitches} switches but no off-CPU spans closed within the window. " +
              "Either no thread blocked, or wakeups landed outside the capture — try a longer durationSeconds.";

        var ok = DiagnosticResult.OkWithHandle(
            inlineSummary,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Drill into per-thread off-CPU view or a specific stack.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "byThread" }),
            new NextActionHint("collect_sample", "Cross-reference with on-CPU hotspots to separate compute from wait.",
                new Dictionary<string, object?> { ["processId"] = pid, ["durationSeconds"] = 10 }));
        return WithContext(ok, resolved.Context);
    }

    /// <summary>Canonical kind tag for handles backing an <see cref="OffCpuSnapshotArtifact"/>.</summary>
    public const string OffCpuHandleKind = "off-cpu-snapshot";

    /// <summary>Canonical kind tag for handles backing a native-allocation call tree
    /// (a <see cref="CpuSampleTraceArtifact"/>); walked with <c>query_snapshot(view="call-tree")</c>.</summary>
    public const string NativeAllocHandleKind = "native-alloc-sample";

    [RequireScope("eventpipe")]
    [Description(
        "Attributes NATIVE (unmanaged) allocations to a call site by uprobing the target's libc " +
        "allocator (malloc/calloc/realloc) with perf and DWARF unwinding. Companion to " +
        "collect_sample(kind='allocation'): the managed sampler only sees the GC heap; this sees " +
        "allocations the runtime makes outside it (P/Invoke, native libraries, the runtime). Use it " +
        "to escalate from sample_memory_trend triangulation (RSS up, managed heap flat → native " +
        "growth) to a concrete call site. " +
        "Hotspot-only (issue #279): counts are sampled allocator-call hits, NOT bytes, and NOT " +
        "alloc/free retention — it shows who allocates most, not what leaks. " +
        "Backend: Linux only. Needs the perf binary plus permission to create a uprobe " +
        "(CAP_SYS_ADMIN / tracefs write access — strictly more than off-CPU's CAP_PERFMON). " +
        "Returns the top-N allocator stacks inline and a handle for the query_snapshot call-tree drilldown.")]
    public static async Task<DiagnosticResult<NativeAllocSample>> CollectNativeAllocSample(
        INativeAllocSampler sampler,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Sampling window in seconds. Must be >= 1. Defaults to 10. Keep short on allocator-hot workloads — uprobe overhead is per-call.")] int durationSeconds = 10,
        [Description("Maximum number of allocator hotspots returned inline (the full call tree lives behind the handle). Defaults to 25.")] int topN = 25,
        [Description("perf sample period — record one callchain per this many allocator hits. Must be >= 1. Defaults to 1000. Higher reduces overhead and resolution; it throttles recorded samples but not the per-call trap cost.")] long samplePeriod = 1000,
        CancellationToken cancellationToken = default)
    {
        if (durationSeconds < 1) return InvalidArg<NativeAllocSample>(nameof(durationSeconds), "must be >= 1");
        if (topN < 1) return InvalidArg<NativeAllocSample>(nameof(topN), "must be >= 1");
        if (samplePeriod < 1) return InvalidArg<NativeAllocSample>(nameof(samplePeriod), "must be >= 1");

        var resolved = await ResolveContextAsync<NativeAllocSample>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        NativeAllocSampleResult result;
        try
        {
            result = await sampler.SampleAsync(pid, TimeSpan.FromSeconds(durationSeconds), topN, samplePeriod, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (NotSupportedException ex)
        {
            return DiagnosticResult.Fail<NativeAllocSample>(
                ex.Message,
                new DiagnosticError("NotSupported", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Confirm the target is a dynamically-linked glibc/musl process; statically-linked or custom-allocator (jemalloc/tcmalloc) targets aren't supported by the libc uprobe path.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("perf", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("uprobe", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("tracefs", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("CAP_", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("paranoid", StringComparison.OrdinalIgnoreCase))
        {
            return DiagnosticResult.Fail<NativeAllocSample>(
                ex.Message,
                new DiagnosticError("PermissionDenied", ex.Message, ex.GetType().FullName),
                new NextActionHint("inspect_process",
                    "Check the capability matrix; install linux-perf and add CAP_SYS_ADMIN to the sidecar securityContext.",
                    new Dictionary<string, object?> { ["processId"] = pid }));
        }

        var sample = result.Summary;
        var handle = handles.Register(pid, NativeAllocHandleKind, result.Artifact, CpuSampleHandleTtl);

        var topAllocator = sample.TopAllocators.Count > 0 ? sample.TopAllocators[0] : null;
        var summaryText = topAllocator is not null
            ? $"Captured {sample.TotalSampledAllocations} sampled native allocator-call(s) over {durationSeconds}s " +
              $"(probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath}, samplePeriod={sample.SamplePeriod}). " +
              $"Top allocator stack: {topAllocator.Frame.Method} ({topAllocator.InclusiveSamples} inclusive hits). " +
              $"Counts are calls, not bytes. Drill into call sites with query_snapshot(handle=\"{handle.Id}\", view=\"call-tree\")."
            : $"Probed {string.Join("/", sample.ProbedFunctions)} in {sample.LibcPath} but captured no native " +
              $"allocator-call samples in {durationSeconds}s — the workload may not allocate natively, or samplePeriod " +
              "is too high. Drive the suspect load during the window or lower samplePeriod.";

        var ok = DiagnosticResult.OkWithHandle(
            sample,
            summaryText,
            handle.Id,
            handle.ExpiresAt,
            new NextActionHint("query_snapshot", "Walk the native allocation call tree to find which code paths allocate the most.",
                new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "call-tree", ["maxDepth"] = 8, ["maxNodes"] = 200 }),
            new NextActionHint("inspect_process", "Correlate with the memory trend (RSS / anonymous pages) to confirm native growth.",
                new Dictionary<string, object?> { ["processId"] = pid, ["view"] = "memory_trend" }));
        return WithContext(ok, resolved.Context);
    }


    [RequireScope("eventpipe")]
    [Description(
        "Re-projects a prior collect_off_cpu_sample artifact under a named view, without re-running perf. " +
        "Views: 'topStacks' (default — blocking stacks ranked by off-CPU micros), 'byThread' (per-TID rollup), " +
        "'stack' (full root→leaf frames of a specific stack rank). Use the handle returned by collect_off_cpu_sample. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<OffCpuQueryView> QueryOffCpuSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_off_cpu_sample call.")] string handle,
        [Description("View name: topStacks (default), byThread, stack.")] string view = "topStacks",
        [Description("Maximum items returned for topStacks/byThread. Defaults to 25.")] int topN = 25,
        [Description("Required when view='stack' — 1-based rank of the stack in the top-stacks list.")] int? stackRank = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<OffCpuQueryView>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<OffCpuQueryView>(nameof(topN), "must be >= 1");

        var artifact = handles.TryGet<OffCpuSnapshotArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<OffCpuQueryView>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Off-CPU handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_sample", "Re-run the off-CPU sampler to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        return view.ToLowerInvariant() switch
        {
            "bythread" => DiagnosticResult.Ok(
                new OffCpuQueryView(view, artifact.ProcessId, artifact.TotalOffCpuMicros,
                    Stacks: null,
                    Threads: artifact.Threads.Take(topN).ToList(),
                    Stack: null),
                $"{Math.Min(topN, artifact.Threads.Count)} of {artifact.Threads.Count} threads ranked by off-CPU micros."),
            "stack" => RenderStack(artifact, stackRank),
            "topstacks" or _ => DiagnosticResult.Ok(
                new OffCpuQueryView(view, artifact.ProcessId, artifact.TotalOffCpuMicros,
                    Stacks: artifact.Stacks.Take(topN).ToList(),
                    Threads: null,
                    Stack: null),
                $"Top {Math.Min(topN, artifact.Stacks.Count)} blocking stacks of {artifact.Stacks.Count} distinct."),
        };
    }

    private static DiagnosticResult<OffCpuQueryView> RenderStack(OffCpuSnapshotArtifact artifact, int? stackRank)
    {
        if (stackRank is null || stackRank < 1)
        {
            return InvalidArg<OffCpuQueryView>(nameof(stackRank), "is required for view='stack' and must be >= 1");
        }
        var idx = stackRank.Value - 1;
        if (idx >= artifact.Stacks.Count)
        {
            return DiagnosticResult.Fail<OffCpuQueryView>(
                $"stackRank={stackRank} exceeds available {artifact.Stacks.Count} stacks.",
                new DiagnosticError("OutOfRange", "Pick a rank within the topStacks list.", stackRank.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new NextActionHint("query_snapshot", "List the top stacks first.",
                    new Dictionary<string, object?> { ["view"] = "topStacks" }));
        }
        var s = artifact.Stacks[idx];
        return DiagnosticResult.Ok(
            new OffCpuQueryView("stack", artifact.ProcessId, artifact.TotalOffCpuMicros,
                Stacks: null, Threads: null, Stack: s),
            $"Rank {stackRank}/{artifact.Stacks.Count}: {s.LeafFrame} — {s.OffCpuMicros / 1000.0:F1} ms across {s.OccurrenceCount} switches (state={s.DominantState}).");
    }

    private static readonly TimeSpan CpuSampleHandleTtl = TimeSpan.FromMinutes(10);

    private static DiagnosticResult<CpuSample> BuildCpuSampleResult(
        CpuSample sample,
        int durationSeconds,
        string handleId,
        DateTimeOffset handleExpiresAt,
        SamplingDepth depth,
        params NextActionHint[] hints)
    {
        var top = sample.TopHotspots.Count > 0 ? sample.TopHotspots[0] : null;
        var inlineSample = sample;
        var droppedHotspots = 0;
        if (depth == SamplingDepth.Summary && sample.TopHotspots.Count > 3)
        {
            droppedHotspots = sample.TopHotspots.Count - 3;
            inlineSample = sample with { TopHotspots = sample.TopHotspots.Take(3).ToArray() };
        }

        var summary = top is not null
            ? (depth == SamplingDepth.Summary && droppedHotspots > 0
                ? $"Captured {sample.TotalSamples} samples over {durationSeconds}s — showing top {inlineSample.TopHotspots.Count} of {sample.TopHotspots.Count} hotspot(s) (dropped {droppedHotspots}; handle has all). Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive). Drill into the full call tree with query_snapshot(handle=\"{handleId}\", view=\"call-tree\")."
                : $"Captured {sample.TotalSamples} samples over {durationSeconds}s. Top method: {top.Frame.Method} ({top.InclusiveSamples} inclusive / {top.ExclusiveSamples} exclusive). Drill into the full call tree with query_snapshot(handle=\"{handleId}\", view=\"call-tree\").")
            : $"Captured {sample.TotalSamples} samples but no method aggregation surfaced — increase durationSeconds or verify the target is under load.";

        return DiagnosticResult.OkWithHandle(inlineSample, summary, handleId, handleExpiresAt, hints);
    }

    [RequireAnyScope("read-counters", "eventpipe")]
    [Description(
        "Re-projects a previously-collected counter/exception/GC/EventSource/Activity/log/JIT/ThreadPool/db artifact under a " +
        "named view, without re-running the underlying EventPipe session. Use the `handle` " +
        "returned by snapshot_counters / collect_exceptions / collect_gc_events / collect_event_source / collect_activities / collect_events(kind=\"logs\") / collect_events(kind=\"jit\") / collect_events(kind=\"threadpool\") / collect_events(kind=\"db\"). " +
        "Supported views per kind: counters → summary|byProvider; exception-snapshot → " +
        "summary|byType|recent; gc-events → summary|events|pauseHistogram; event-source → " +
        "summary|byEventName|events; activities → summary|bySource|byOperation|activities|gc-overlay; " +
        "log-snapshot → summary|byCategory|byLevel|recent|errors; jit-snapshot → summary|topMethods|tierDistribution|reJIT; threadpool-snapshot → summary|timeline|hillClimbing|workItemOrigins; db-snapshot → summary|byCommand|n+1|connectionPool. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<CollectionQueryResult> QueryCollection(
        IDiagnosticHandleStore handles,
        IPrincipalAccessor principalAccessor,
        [Description("Handle returned by a prior collection tool (snapshot_counters / collect_exceptions / collect_gc_events / collect_event_source / collect_activities / collect_events(kind=\"logs\") / collect_events(kind=\"jit\") / collect_events(kind=\"threadpool\") / collect_events(kind=\"db\")). ")] string handle,
        [Description("View name (kind-dependent). Defaults to 'summary'.")] string? view = null,
        [Description("Cap on inline items for paginated views (recent / events / byType / byEventName / bySource / byOperation / activities / byCategory / byLevel / errors / topMethods / reJIT / hillClimbing / workItemOrigins / byCommand / n+1 / connectionPool). Must be >= 1. Defaults to 50.")] int topN = 50,
        [Description("Handle to a gc-events artifact for correlation views (required for activities view='gc-overlay').")] string? gcHandle = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<CollectionQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<CollectionQueryResult>(nameof(topN), "must be >= 1");

        var entry = handles.TryGetWithKind(handle);
        if (entry is null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError(
                    "HandleExpired",
                    "Collection handles live ~10min and are invalidated when the target process exits.",
                    handle),
                new NextActionHint("collect_events", "Re-run the original collector on the same pid to issue a fresh handle.", null));
        }

        // Look up correlation artifact if gcHandle is provided
        object? correlateArtifact = null;
        if (!string.IsNullOrWhiteSpace(gcHandle))
        {
            var gcEntry = handles.TryGetWithKind(gcHandle);
            if (gcEntry is null)
            {
                return DiagnosticResult.Fail<CollectionQueryResult>(
                    $"gcHandle '{gcHandle}' is unknown or expired.",
                    new DiagnosticError(
                        "HandleExpired",
                        "GC handle has expired. Re-run collect_events(kind='gc') to get a fresh handle.",
                        gcHandle),
                    new NextActionHint("collect_events", "Capture new GC events with kind='gc'.", new Dictionary<string, object?> { ["kind"] = "gc" }));
            }
            if (gcEntry.Value.Kind != CollectionHandleKinds.GcEvents)
            {
                return DiagnosticResult.Fail<CollectionQueryResult>(
                    $"gcHandle '{gcHandle}' is not a gc-events artifact (kind='{gcEntry.Value.Kind}').",
                    new DiagnosticError(
                        "InvalidHandle",
                        "gcHandle must point to a gc-events artifact from collect_events(kind='gc').",
                        gcHandle));
            }
            correlateArtifact = gcEntry.Value.Artifact;
        }

        var principal = principalAccessor.Current;
        if (principal is not null)
        {
            var requiredScope = entry.Value.Kind == CollectionHandleKinds.Counters ? "read-counters" : "eventpipe";
            if (!principal.HasScope(requiredScope))
            {
                var message = $"forbidden: tool 'query_collection' requires scope '{requiredScope}' for kind '{entry.Value.Kind}'.";
                return DiagnosticResult.Fail<CollectionQueryResult>(
                    message,
                    new DiagnosticError("Forbidden", message, requiredScope));
            }
        }

        var outcome = CollectionQueryDispatcher.Dispatch(entry.Value.Kind, view, entry.Value.Artifact, topN, correlateArtifact);

        if (outcome.UnknownKind is not null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Handle '{handle}' is of kind '{outcome.UnknownKind}' which query_collection does not support.",
                new DiagnosticError(
                    "UnsupportedHandleKind",
                    $"query_collection dispatches over kinds: {string.Join(", ", new[] { CollectionHandleKinds.Counters, CollectionHandleKinds.ExceptionSnapshot, CollectionHandleKinds.GcEvents, CollectionHandleKinds.EventSource, CollectionHandleKinds.Activities, CollectionHandleKinds.LogSnapshot, CollectionHandleKinds.JitSnapshot, CollectionHandleKinds.ThreadPoolSnapshot, CollectionHandleKinds.DbSnapshot })}.",
                    outcome.UnknownKind),
                new NextActionHint("query_snapshot", "Use the kind-specific drill-down tool for heap/thread/cpu handles.", null));
        }
        if (outcome.UnknownView is not null)
        {
            var allowed = outcome.AllowedViews ?? Array.Empty<string>();
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"View '{outcome.UnknownView}' is not defined for kind '{entry.Value.Kind}'.",
                new DiagnosticError(
                    "UnknownView",
                    $"Allowed views: {string.Join(", ", allowed)}.",
                    outcome.UnknownView),
                new NextActionHint("query_snapshot", "Retry with one of the allowed views.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = allowed.Count > 0 ? allowed[0] : "summary" }));
        }
        if (outcome.InvalidArgument is not null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Invalid argument: {outcome.InvalidArgument}.",
                new DiagnosticError("InvalidArgument", outcome.InvalidArgument, nameof(topN)));
        }

        var result = outcome.Result!;
        return DiagnosticResult.Ok(
            result,
            $"Rendered view '{result.View}' for kind '{result.Kind}' (collected {result.Duration.TotalSeconds:F1}s starting {result.StartedAt:HH:mm:ss}Z, pid {result.ProcessId}).",
            new NextActionHint("query_snapshot",
                $"Switch to another view: {string.Join(" | ", CollectionQueryDispatcher.ViewsFor(result.Kind))}.",
                new Dictionary<string, object?> { ["handle"] = handle }));
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes to the runtime Exception keyword on Microsoft-Windows-DotNETRuntime and " +
        "captures every managed exception thrown by the target process during the window. " +
        "Returns total count (always exact), breakdown by exception type (always exact), and " +
        "the first maxRecent individual exception details — when TotalExceptions exceeds " +
        "maxRecent the Recent list is truncated to the head of the stream (the cap that was " +
        "applied is echoed back as ExceptionSnapshot.RecentCap). " +
        "Spec-compliant clients can call this tool as an MCP Task and poll via tasks/get + tasks/result. " +
        "IMPORTANT: start this BEFORE the workload you want to observe — exceptions before the session opens are missed.")]
    public static async Task<DiagnosticResult<ExceptionSnapshot>> CollectExceptions(
        IExceptionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of individual exception details to return. Must be >= 1. Defaults to 100.")] int maxRecent = 100,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Recent[] list inline (keeps Total + ByType, which is what most diagnoses need). 'detail' includes Recent up to maxRecent. 'raw' is equivalent to detail. The full snapshot is always retained behind the issued handle — drill in with query_collection(handle, view=recent).")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectExceptions(
            collector, resolver, handles,
            processId, durationSeconds, maxRecent, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes to the runtime GC keyword and pairs GCStart/GCStop events to compute pause " +
        "durations per collection. Returns total collections, total/max pause time, counts per " +
        "generation, and a bounded list of individual GC events. Spec-compliant clients can call " +
        "this tool as an MCP Task and poll via tasks/get + tasks/result.")]
    public static async Task<DiagnosticResult<GcSummary>> CollectGcEvents(
        IGcCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of GC events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Events[] list inline (keeps totals, max pause, per-gen counts). 'detail' includes Events up to maxEvents. 'raw' is equivalent to detail. The full GC summary is always retained behind the issued handle — drill in with query_collection(handle, view=events|pauseHistogram).")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectGcEvents(
            collector, resolver, handles,
            processId, durationSeconds, maxEvents, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes the Microsoft-Extensions-Logging EventSource and returns a curated ILogger view: " +
        "level counts, top categories, a bounded recent ring buffer, and optional exception/scope JSON when depth != summary.")]
    public static async Task<DiagnosticResult<LogSnapshot>> CollectLogs(
        ILogCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Optional case-insensitive glob filters for ILogger categories. Null/empty captures all categories.")] IReadOnlyList<string>? categories = null,
        [Description("Minimum log level to retain (Trace|Debug|Information|Warning|Error|Critical). Defaults to Information.")] string minLevel = "Information",
        [Description("Maximum number of recent log entries to retain in the in-memory ring buffer. Must be >= 1. Defaults to 500.")] int maxEvents = 500,
        [Description("Maximum UTF-8 bytes retained per message/scope/exception string before truncation. Must be >= 16. Defaults to 4096.")] int maxMessageBytes = 4096,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Recent[] list inline and subscribes only FormattedMessage; 'detail' and 'raw' also enable MessageJson so exception detail and scopes are retained.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectLogs(
            collector, resolver, handles,
            processId, durationSeconds, categories, minLevel, maxEvents, maxMessageBytes, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures CLR JIT / tiered-compilation activity via the Microsoft-Windows-DotNETRuntime provider. " +
        "Reconstructs inclusive JIT time from MethodJittingStarted→MethodLoadVerbose, tracks Tier0/Tier1/ReadyToRun distribution, R2R hit vs miss-then-jit, ReJIT and OSR counts, and returns a drill-down handle.")]
    public static async Task<DiagnosticResult<JitSnapshot>> CollectJit(
        IJitCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' trims the inline method list to the hottest 10 entries; the handle retains every observed method.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectJit(
            collector, resolver, handles,
            processId, durationSeconds, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Subscribes to the runtime ThreadingKeyword and returns a curated ThreadPool starvation view: worker + IOCP timelines, hill-climbing transitions, work-item origins, and best-effort effective min/max settings.")]
    public static async Task<DiagnosticResult<ThreadPoolEventSnapshot>> CollectThreadPool(
        IThreadPoolCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps the headline counts + top origins inline and relies on query_snapshot(handle, view=timeline|hillClimbing|workItemOrigins) for the full drilldown.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectThreadPool(
            collector, resolver, handles,
            processId, durationSeconds, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Collects a curated CLR lock-contention view from the runtime Contention keyword. " +
        "Aggregates monitor waits by call site and owner thread, computes total/p50/p95/max wait duration, and retains the full event slice behind the issued handle for query_snapshot(handle, view=summary|byCallSite|byOwner).")]
    public static async Task<DiagnosticResult<ContentionSnapshot>> CollectContention(
        IContentionCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps the headline aggregates inline and relies on query_snapshot(handle, view=byCallSite|byOwner) for grouped drilldown.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectContention(
            collector, resolver, handles,
            processId, durationSeconds, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Collects a curated DB view by subscribing to EF Core command diagnostics plus SqlClient command/pool signals. " +
        "Aggregates by sanitized command hash + sanitized connection string, computes count/total/max/p95 latency, " +
        "detects N+1 patterns when the same command repeats >10 times in one parent activity, and snapshots SqlClient pool counters. " +
        "The full artifact is always retained behind the issued handle — drill in with query_snapshot(handle, view=summary|byCommand|n+1|connectionPool).")]
    public static async Task<DiagnosticResult<DbSnapshot>> CollectDb(
        IDbCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Refresh interval (in seconds) requested from SqlClient EventCounters. Defaults to 1.")] int intervalSeconds = 1,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps only the top command slice inline; 'detail' and 'raw' return the full by-command and N+1 lists captured in the window.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectDb(
            collector, resolver, handles,
            processId, durationSeconds, intervalSeconds, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures completed ActivitySource spans via the Microsoft-Diagnostics-DiagnosticSource EventPipe bridge. " +
        "Enables the runtime provider with FilterAndPayloadSpecs, extracts operation/trace/span ids, parent linkage, tags, and duration from Activity stop events, aggregates them by source and operation, " +
        "and returns a handle for query_collection drilldown.")]
    public static async Task<DiagnosticResult<ActivityCapture>> CollectActivities(
        IActivityCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Optional ActivitySource name filters. Supports '*' and '?' wildcards. Null/empty captures all sources.")]
        IReadOnlyList<string>? sources = null,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Maximum number of captured activities to retain. Must be >= 1. Defaults to 200.")] int maxActivities = 200,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectActivities(
            collector, resolver, handles,
            processId, sources, durationSeconds, maxActivities,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Generic EventSource passthrough: opens an EventPipe session for a single EventSource " +
        "by name (e.g. System.Net.Http, Microsoft.AspNetCore.Hosting, Microsoft-AspNetCore-Server-Kestrel, " +
        "or any user-defined source) and returns the events emitted during the window. Use this to " +
        "investigate HTTP activity, hosting events, or domain-specific instrumentation.")]
    public static async Task<DiagnosticResult<EventSourceCapture>> CollectEventSource(
        IEventSourceCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        EventSourceAllowlist allowlist,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("EventSource provider name, e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'. Must be on the curated allowlist (see `Diagnostics:EventSourceAllowlist`) unless the bearer principal holds the 'eventsource-any' scope (RFC 0001 §2.3 / B5.4) — or, on legacy deployments, `unsafeProvider=true` AND the server has `Diagnostics:AllowSensitiveHeapValues=true` (issue #165 / M2).")] string providerName,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the capture window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("EventSource keyword mask. -1 (default) means all keywords. For non-allowlisted providers (when opted in via unsafeProvider=true) this is clamped to a safer default when left at -1; pass an explicit positive mask to override.")] long keywords = -1,
        [Description("Event verbosity level (0=LogAlways, 1=Critical, 2=Error, 3=Warning, 4=Informational, 5=Verbose). Defaults to 5. For non-allowlisted providers (when opted in via unsafeProvider=true) this is clamped to Informational unless explicitly set lower.")] int eventLevel = 5,
        [Description("Maximum number of captured events to return. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the Events[] list inline (keeps the Total count and metadata). 'detail' includes Events up to maxEvents. 'raw' is equivalent to detail. The full capture is always retained behind the issued handle — drill in with query_collection(handle, view=byEventName|events).")]
        SamplingDepth depth = SamplingDepth.Summary,
        [Description("Opt-in switch for non-allowlisted EventSource providers (issue #165 / M2). Only honoured when the server has `Diagnostics:AllowSensitiveHeapValues=true`. Defaults to false; deny path returns an `EventSourceProviderNotAllowed` envelope.")] bool unsafeProvider = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectEventSource(
            collector, resolver, handles, allowlist, sensitiveGate,
            principalAccessor.Current?.HasExplicitScope("eventsource-any") == true,
            providerName, processId, durationSeconds, keywords, eventLevel, maxEvents, depth,
            unsafeProvider, deprecation, cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("dump-write", "ptrace")]
    [McpServerTool(
        Name = "collect_process_dump",
        Title = "Write process dump",
        Destructive = true,
        ReadOnly = false,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Writes a process dump for the target .NET application to disk. The dump file remains on the " +
        "server's filesystem (path returned) so it can be analyzed offline with dotnet-dump or WinDbg. " +
        "Dump types in increasing size/cost: Mini < Triage < WithHeap < Full. " +
        "Heavyweight — use only when live collectors are insufficient. " +
        "**Requires `confirm=true` (defense in depth — RFC 0001 §4 / B5.6).** Without it the tool " +
        "returns a `confirmation_required` envelope describing what would have been written and " +
        "writes nothing to disk; the operator-facing client should surface this preview to a human " +
        "and only retry with `confirm=true` after explicit approval. The `dump-write` + `ptrace` " +
        "scopes are still required on top of `confirm=true`.")]
    public static Task<DiagnosticResult<DumpToolResult>> CollectProcessDump(
        IProcessDumper dumper,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        ILoggerFactory? loggerFactory = null,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). Dump files are written with POSIX mode 0600.")] string? outputDirectory = null,
        [Description("Defense-in-depth confirmation flag. Must be true to actually write a dump file; without it the tool returns a `confirmation_required` envelope describing what would have been written. See RFC 0001 §4.")] bool confirm = false,
        CancellationToken cancellationToken = default)
        // Orchestration lives in Core (UseCases/ProcessDumpUseCases) since #288 PR3b. This thin
        // forward creates the logger with the existing audit category and hoists the audit principal
        // name so the confirmation-required envelope + audit log stay byte-identical.
        => ProcessDumpUseCases.CollectProcessDump(
            dumper,
            resolver,
            loggerFactory?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.CollectProcessDump"),
            principalAccessor.Current?.Name,
            processId,
            dumpType,
            outputDirectory,
            confirm,
            cancellationToken);

    [RequireScope("heap-read")]
    [Description(
        "Walks the managed heap of a previously-captured WithHeap/Full dump (produced by " +
        "collect_process_dump or any compatible source) using ClrMD. Returns aggregated runtime/heap " +
        "totals plus top types by retained bytes and instance count. Each TypeStat carries a TypeIdentity " +
        "(ModuleVersionId + MetadataToken) ready to hand off verbatim to dotnet-assembly-mcp's get_type " +
        "(or get_method on the type's members) without name parsing. Offline and read-only — does not " +
        "touch the live process. Mini and Triage dumps return runtime metadata only; for heap inspection " +
        "use WithHeap or Full.")]
    public static Task<DiagnosticResult<DumpInspection>> InspectDump(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Absolute path to a previously-captured .dmp file. Required.")] string dumpFilePath,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; adds an extra pass over AppDomains × Modules × Types.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        // Orchestration lives in Core (UseCases/HeapInspectionUseCases) since #288 PR3b. This thin
        // forward hoists the `symbols-remote` scope decision into a bool and adapts the deprecation
        // singleton onto the host-neutral ISymbolServerDeprecationSink.
        => HeapInspectionUseCases.InspectDump(
            inspector,
            handles,
            symbolServerAllowlist,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            dumpFilePath,
            topTypes,
            includeRetentionPaths,
            retentionPathLimit,
            includeStaticFields,
            includeDelegateTargets,
            includeDuplicateStrings,
            symbolPath,
            deprecation,
            cancellationToken);

    // Re-exported from Core (UseCases/HeapInspectionUseCases) so QuerySnapshotTool keeps addressing
    // heap snapshots by the same kind tag after the #288 PR3b extraction.
    internal const string HeapSnapshotKind = HeapInspectionUseCases.HeapSnapshotKind;

    [RequireScope("heap-read", "ptrace")]
    [Description(
        "Attaches to a live .NET process via ClrMD and walks its managed heap WITHOUT writing a dump file. " +
        "Returns the same top-N type / retention information as inspect_dump but skips the disk I/O of " +
        "collect_process_dump. The target is suspended for the duration of the walk (typically sub-second " +
        "for small heaps, can reach a few seconds for multi-GB heaps); plan accordingly for latency-sensitive " +
        "workloads. Same UID constraint as the diagnostic socket applies — sidecar must run as the target's UID. " +
        "Each TypeStat carries a TypeIdentity (ModuleVersionId + MetadataToken) ready to hand off verbatim to " +
        "dotnet-assembly-mcp's get_type. Use inspect_dump when you need an artifact to keep, share or re-inspect.")]
    public static Task<DiagnosticResult<LiveHeapInspection>> InspectLiveHeap(
        IDumpInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Number of types to return in each top-N list (bytes / instances). Defaults to 20.")] int topTypes = 20,
        [Description("When true, walks a short GC retention chain for the top retained types. Off by default — slower and lengthens the suspend window.")] bool includeRetentionPaths = false,
        [Description("Cap on retention-chain depth when retention paths are enabled. Defaults to 8.")] int retentionPathLimit = 8,
        [Description("When true, enumerate every loaded type's static reference fields ranked by directly-referenced object size — surfaces 'singleton that grew forever' leaks. Off by default; lengthens the suspend window.")] bool includeStaticFields = false,
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).")] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        // Orchestration lives in Core (UseCases/HeapInspectionUseCases) since #288 PR3b.
        => HeapInspectionUseCases.InspectLiveHeap(
            inspector,
            handles,
            resolver,
            symbolServerAllowlist,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            processId,
            topTypes,
            includeRetentionPaths,
            retentionPathLimit,
            includeStaticFields,
            includeDelegateTargets,
            includeDuplicateStrings,
            symbolPath,
            deprecation,
            cancellationToken);

    [RequireScope("heap-read")]
    [Description(
        "Returns a slice of a heap snapshot previously captured by inspect_dump or inspect_live_heap, addressed by its handle. " +
        "Lets the LLM ask for a richer top-N (snapshot retains ~200 types), retention paths filtered by type substring, " +
        "GC roots grouped by kind, the finalizer queue, or per-segment heap layout — without paying the walk cost a second time. Views: " +
        "`top-types` (expand the inline top-N to up to snapshot capacity), " +
        "`retention-paths` (filter the walked retention chains by target type substring; requires the original inspect call to have set includeRetentionPaths=true), " +
        "`roots-by-kind` (GC roots aggregated by ClrRootKind with pinned/interior counts), " +
        "`finalizer-queue` (objects waiting for finalization, top-N by retained bytes), " +
        "`fragmentation` (per-segment Gen/Kind/Length/Committed/Free bytes — high FreePercent on Gen2/LOH signals fragmentation), " +
        "`static-fields` (top static reference fields by directly-referenced object size — requires the original inspect call to have set includeStaticFields=true), " +
        "`delegate-targets` (delegate / event-handler subscribers grouped by (target type, method) — requires includeDelegateTargets=true), " +
        "`duplicate-strings` (duplicate System.String contents ranked by aggregate retained bytes — requires includeDuplicateStrings=true), " +
        "`gchandles` (GCHandle table aggregated by public GCHandleType-compatible buckets with top target types), " +
        "`object` (dump one managed object by address — SOS !do equivalent), " +
        "`gcroot` (find a shortest GC-root chain for one object address — SOS !gcroot equivalent), " +
        "`objsize` (compute the transitive retained size rooted at one object address — SOS !objsize equivalent), " +
        "`async` (pending async state machines reconstructed from the heap — state, awaiter type, and best-effort continuation chain à la SOS DumpAsync). " +

        "Handles expire ~10 minutes after the capture and are invalidated when the target process exits (live origin only).")]
    public static async Task<DiagnosticResult<HeapSnapshotQueryResult>> QueryHeapSnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("Snapshot handle returned by inspect_dump or inspect_live_heap.")] string handle,
        [Description("Which slice of the snapshot to return: 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'object', 'gcroot', 'objsize' or 'async'.")] string view = "top-types",
        [Description("Maximum entries to return for any ranked view ('top-types', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'async'). Ignored by 'roots-by-kind', 'gchandles', 'retention-paths', 'object', 'gcroot' and 'objsize'.")] int topN = 50,
        [Description("For view='top-types': ranking — 'bytes' (default) or 'instances'.")] string rankBy = "bytes",
        [Description("For view='retention-paths': case-insensitive substring matched against TypeFullName to narrow the returned chains.")] string? typeFullName = null,
        [Description("For view='object', 'gcroot' and 'objsize': managed object address (decimal or 0x-prefixed hex).")] string? address = null,
        [Description("Opt-in to return raw string content / field value previews on the 'duplicate-strings' and 'object' views (issue #165 / H4). Defaults to false — those fields are returned as metadata-only placeholders unless the server enables `Diagnostics:AllowSensitiveHeapValues=true` AND the caller passes `includeSensitiveValues=true`. Any string surfaced even in that mode still runs through the SensitiveDataRedactor (Bearer/PEM/JWT/connection-string/AWS-key patterns).")] bool includeSensitiveValues = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<HeapSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<HeapSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<HeapSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Heap snapshot handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("inspect_heap", "Re-attach and re-walk to issue a fresh handle.",
                    new Dictionary<string, object?> { ["processId"] = "<pid>" }));
        }

        // RFC 0001 §2.4: the legacy AllowSensitiveHeapValues flag is the deployment-wide gate;
        // a principal holding the 'sensitive-heap-read' scope opts in per-bearer (B5.2). The
        // caller still has to pass includeSensitiveValues=true to actually receive raw content.
        var principalUnlocksSensitive = principalAccessor.Current?.HasExplicitScope("sensitive-heap-read") == true;
        var emitSensitive = sensitiveGate.ShouldEmit(includeSensitiveValues, principalUnlocksSensitive);

        // B5.4: warn once per process if the legacy server flag (rather than the
        // 'sensitive-heap-read' scope) is what unlocked sensitive-value emission.
        if (emitSensitive && !principalUnlocksSensitive && sensitiveGate.IsAllowedByServer)
        {
            deprecation?.NotifySensitiveHeapValuesFlagBypass();
        }

        var normalizedView = view.Trim().ToLowerInvariant();

        // The 9 projection views (renderable from the walked snapshot alone) are rendered by the
        // host-neutral Core dispatcher, shared with the CLI `session` REPL (#300). The 4
        // capability-bound views below stay server-owned: duplicate-strings needs the sensitive-value
        // redactor + gate, and object/gcroot/objsize need a live ClrMD attach behind the auth guard.
        var projection = HeapSnapshotQueryDispatcher.Dispatch(snapshot, handle, normalizedView, topN, rankBy, typeFullName);
        if (projection.Result is { } projected)
        {
            return projected;
        }

        switch (normalizedView)
        {
            case "duplicate-strings":
                return QueryDuplicateStrings(snapshot, handle, topN, redactor, emitSensitive);
            case "object":
            case "gcroot":
            case "objsize":
                if (string.IsNullOrWhiteSpace(address)) return InvalidArg<HeapSnapshotQueryResult>(nameof(address), $"is required for view='{normalizedView}'");
                if (!TryParseUnsignedHexOrInt(address, out var parsedAddress) || parsedAddress == 0)
                {
                    return InvalidArg<HeapSnapshotQueryResult>(nameof(address), "must be a non-zero address (decimal or 0x-prefixed hex)");
                }

                return await GuardAttachAsync(
                    "query_heap_snapshot",
                    snapshot.Origin == HeapSnapshotOrigin.Live ? snapshot.ProcessId : null,
                    async () => normalizedView switch
                    {
                        "object" => QueryObject(snapshot, handle, await inspector.InspectObjectAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false), redactor, emitSensitive),
                        "gcroot" => QueryGcRoot(snapshot, handle, await inspector.InspectGcRootAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false)),
                        _ => QueryObjectSize(snapshot, handle, await inspector.InspectObjectSizeAsync(snapshot, parsedAddress, cancellationToken).ConfigureAwait(false)),
                    },
                    cancellationToken).ConfigureAwait(false);
            default:
                return InvalidArg<HeapSnapshotQueryResult>(nameof(view), $"must be 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'object', 'gcroot', 'objsize' or 'async' (got '{view}')");
        }
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryObject(
        HeapSnapshotArtifact snapshot, string handle, HeapObjectInspection inspection, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        var origin = snapshot.Origin.ToString();
        var sanitized = SanitizeObjectInspection(inspection, redactor, emitSensitive);
        var summary = $"Returning object 0x{sanitized.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{sanitized.TypeFullName}` ({sanitized.Size:N0} bytes, {sanitized.Generation}/{sanitized.SegmentKind}).";
        if (!emitSensitive && (inspection.IsString || (inspection.Fields is { Count: > 0 })))
        {
            summary += " String/field value previews are redacted (issue #165 / H4); pass includeSensitiveValues=true on a server with Diagnostics:AllowSensitiveHeapValues=true to opt in.";
        }
        if (snapshot.Origin == HeapSnapshotOrigin.Live)
        {
            summary += " Live-object addresses can move after a GC; re-run inspect_live_heap if this address stops resolving.";
        }

        var result = new HeapSnapshotQueryResult(handle, "object", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = sanitized.Address,
            ObjectDetails = sanitized,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    // B4 / issue #165 / H4: rewrite an inspector-produced HeapObjectInspection so that
    // (a) when the sensitive-value gate is closed, every string preview and field value
    // is replaced with a metadata-only placeholder; (b) when the gate is open, every
    // string still passes through the redactor pattern set.
    private static HeapObjectInspection SanitizeObjectInspection(HeapObjectInspection inspection, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        IReadOnlyList<HeapObjectField>? fields = inspection.Fields;
        if (fields is { Count: > 0 })
        {
            var sanitizedFields = new List<HeapObjectField>(fields.Count);
            foreach (var f in fields)
            {
                var value = emitSensitive ? (redactor.Redact(f.Value) ?? f.Value) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
                sanitizedFields.Add(new HeapObjectField(f.Name, f.TypeFullName, value)
                {
                    ObjectAddress = f.ObjectAddress,
                    ReferencedTypeFullName = f.ReferencedTypeFullName,
                });
            }
            fields = sanitizedFields;
        }

        IReadOnlyList<HeapArrayElement>? array = inspection.ArraySample;
        if (array is { Count: > 0 })
        {
            var sanitizedArray = new List<HeapArrayElement>(array.Count);
            foreach (var a in array)
            {
                var value = emitSensitive ? (redactor.Redact(a.Value) ?? a.Value) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
                sanitizedArray.Add(new HeapArrayElement(a.Index, a.TypeFullName, value)
                {
                    ObjectAddress = a.ObjectAddress,
                    ReferencedTypeFullName = a.ReferencedTypeFullName,
                });
            }
            array = sanitizedArray;
        }

        string? stringValue = inspection.StringValue;
        if (inspection.IsString)
        {
            stringValue = emitSensitive ? redactor.Redact(stringValue) : SensitiveDataRedactor.MetadataOnlyPlaceholder;
        }

        return new HeapObjectInspection(inspection.Address, inspection.TypeFullName, inspection.Size, inspection.SegmentKind, inspection.Generation)
        {
            IsArray = inspection.IsArray,
            ArrayLength = inspection.ArrayLength,
            ArraySample = array,
            IsString = inspection.IsString,
            StringValue = stringValue,
            StringValueTruncated = inspection.StringValueTruncated,
            Fields = fields,
            Warnings = inspection.Warnings,
        };
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryGcRoot(
        HeapSnapshotArtifact snapshot, string handle, HeapGcRootInspection inspection)
    {
        var origin = snapshot.Origin.ToString();
        var summary = $"Returning GC-root chain for 0x{inspection.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{inspection.TypeFullName}` with {inspection.Chain.Count:N0} frame(s).";
        if (inspection.Truncated)
        {
            summary += " Chain is truncated by the BFS/depth safety caps.";
        }

        var result = new HeapSnapshotQueryResult(handle, "gcroot", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = inspection.Address,
            GcRoot = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryObjectSize(
        HeapSnapshotArtifact snapshot, string handle, HeapObjectSizeInspection inspection)
    {
        var origin = snapshot.Origin.ToString();
        var summary = $"Returning object graph size for 0x{inspection.Address:x} from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) — `{inspection.TypeFullName}` retains {inspection.RetainedBytes:N0} bytes across {inspection.ObjectCount:N0} object(s).";
        if (inspection.Truncated)
        {
            summary += " Result is truncated by the safety cap and is therefore a lower bound.";
        }

        var result = new HeapSnapshotQueryResult(handle, "objsize", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            Address = inspection.Address,
            ObjectSize = inspection,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static DiagnosticResult<HeapSnapshotQueryResult> QueryDuplicateStrings(
        HeapSnapshotArtifact snapshot, string handle, int topN, SensitiveDataRedactor redactor, bool emitSensitive)
    {
        var origin = snapshot.Origin.ToString();
        if (snapshot.DuplicateStrings is null)
        {
            return DiagnosticResult.Fail<HeapSnapshotQueryResult>(
                $"Snapshot '{handle}' was captured without duplicate-string aggregation.",
                new DiagnosticError("ViewNotCaptured", "Re-run inspect_dump / inspect_live_heap with includeDuplicateStrings=true.", handle));
        }
        // B4 / issue #165 / H4: previews routinely contain secrets. Default to metadata-only
        // (StringLength + InstanceCount + TotalBytes survive — they're enough to identify
        // string-interning opportunities). When the caller opts in and the server gate is
        // open, redact known secret shapes before returning the preview.
        var slice = snapshot.DuplicateStrings.Take(topN).Select(s =>
        {
            string preview;
            if (emitSensitive)
            {
                preview = redactor.Redact(s.Preview) ?? s.Preview;
            }
            else
            {
                preview = SensitiveDataRedactor.MetadataOnlyPlaceholder;
            }
            return new DuplicateStringStat(preview, s.StringLength, s.InstanceCount, s.TotalBytes, s.PreviewTruncated);
        }).ToArray();
        var summary = slice.Length == 0
            ? $"Snapshot '{handle}' has no duplicated System.String contents."
            : (emitSensitive
                ? $"Returning {slice.Length} duplicated string(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes (length {slice[0].StringLength}). Previews pass through the SensitiveDataRedactor (Bearer/PEM/JWT/conn-string patterns) — consider string.Intern() / a cache for the hottest entries."
                : $"Returning {slice.Length} duplicated string(s) (metadata-only — string previews redacted per issue #165 / H4) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}). Top waste: {slice[0].InstanceCount:N0} copies, {slice[0].TotalBytes:N0} bytes (length {slice[0].StringLength}). Pass includeSensitiveValues=true on a server with Diagnostics:AllowSensitiveHeapValues=true to reveal previews.");
        var result = new HeapSnapshotQueryResult(handle, "duplicate-strings", origin, snapshot.ProcessId, snapshot.CapturedAt)
        {
            DuplicateStrings = slice,
        };
        return DiagnosticResult.Ok(result, summary);
    }

    private static readonly TimeSpan ThreadSnapshotHandleTtl = TimeSpan.FromMinutes(10);
    internal const string ThreadSnapshotKind = "thread-snapshot";

    [RequireScope("ptrace")]
    [McpServerTool(
        Name = "collect_thread_snapshot",
        Title = "Capture managed threads + locks from a live process or dump",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Captures a single-point-in-time snapshot of all managed threads (state, stack frames with " +
        "MethodIdentity handoff, inferred wait reason) plus the SyncBlock-based lock graph (object " +
        "address, owning thread, waiter count). Supply at most ONE of processId or dumpFilePath: " +
        "processId attaches via ClrMD with suspend (typically sub-second on ≤100 threads); " +
        "dumpFilePath analyses an already-captured WithHeap/Full dump offline. When both are omitted " +
        "the server auto-selects a live .NET process (live mode). Returns inline threads-summary + " +
        "lock-graph headlines plus a handle (~10min TTL) the LLM can drill into via " +
        "query_thread_snapshot. Dump-origin handles are NOT evicted when the producer PID exits.")]
    public static async Task<DiagnosticResult<ThreadSnapshotQueryResult>> CollectThreadSnapshot(
        IThreadSnapshotInspector inspector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        SymbolServerAllowlist symbolServerAllowlist,
        IPrincipalAccessor principalAccessor,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Maximum stack frames captured per thread. Defaults to 64.")] int maxFramesPerThread = 64,
        [Description("Include runtime frames (PInvoke trampolines, etc.) without an associated managed method. Off by default.")] bool includeRuntimeFrames = false,
        [Description("Include pure native frames where ClrMD cannot resolve a method. Off by default.")] bool includeNativeFrames = false,
        [Description("Optional NT_SYMBOL_PATH-style search path forwarded to symbol-resolving backends. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns only the top-3 blocked threads inline and drops the SyncBlock lock-graph (use query_thread_snapshot(view=lock-graph) for the full graph). 'detail' returns the historical top-25 threads + top-25 locks. 'raw' is equivalent to detail. The full snapshot is always retained behind the issued handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
    {
        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }
        if (maxFramesPerThread < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), "must be >= 1");
        if (maxFramesPerThread > ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap) return InvalidArg<ThreadSnapshotQueryResult>(nameof(maxFramesPerThread), $"must be <= {ClrMdThreadSnapshotInspector.MaxFramesPerThreadHardCap} (bounds the live-attach suspend window)");

        // B4 / issue #165 / M3: same SSRF guard as collect_cpu_sample.
        var symbolDenial = ValidateSymbolPath<ThreadSnapshotQueryResult>(symbolServerAllowlist, symbolPath, principalAccessor, deprecation);
        if (symbolDenial is not null) return symbolDenial;

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            // Live path — auto-resolve when caller omitted processId.
            var resolved = await ResolveContextAsync<ThreadSnapshotQueryResult>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        return await GuardAttachAsync("collect_thread_snapshot", hasDump ? (int?)null : livePid, async () =>
        {
            var opts = new ThreadSnapshotOptions(maxFramesPerThread, includeRuntimeFrames, includeNativeFrames, symbolPath);
            ThreadSnapshotArtifact snapshot;
            bool evictOnExit;
            if (hasDump)
            {
                snapshot = await inspector.InspectDumpAsync(dumpFilePath!, opts, cancellationToken).ConfigureAwait(false);
                evictOnExit = false;
            }
            else
            {
                snapshot = await inspector.InspectLiveAsync(livePid, opts, cancellationToken).ConfigureAwait(false);
                evictOnExit = true;
            }

            var handle = handles.Register(snapshot.ProcessId, ThreadSnapshotKind, snapshot, ThreadSnapshotHandleTtl, evictWhenProcessExits: evictOnExit);
            var origin = snapshot.Origin.ToString().ToLowerInvariant();
            var blocked = snapshot.Threads.Count(t => t.IsLikelyBlocked);
            var contended = snapshot.Locks.Count(l => l.IsContended);
            ThreadSnapshotQueryResult summaryView;
            string summary;
            if (depth == SamplingDepth.Summary)
            {
                var topBlocked = snapshot.Threads
                    .OrderByDescending(t => t.IsLikelyBlocked)
                    .ThenByDescending(t => t.LockCount)
                    .Take(3)
                    .ToArray();
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = topBlocked,
                    Locks = Array.Empty<MonitorLockState>(),
                };
                var droppedThreads = snapshot.Threads.Count - topBlocked.Length;
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Showing top {topBlocked.Length} blocked inline (dropped {droppedThreads} thread(s) and {snapshot.Locks.Count} lock(s); handle has all). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_thread_snapshot(view=top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool).";
            }
            else
            {
                summaryView = new ThreadSnapshotQueryResult(handle.Id, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
                {
                    Threads = snapshot.Threads.Take(25).ToArray(),
                    Locks = snapshot.Locks.Take(25).ToArray(),
                };
                summary = $"{origin} thread snapshot of pid {snapshot.ProcessId}: {snapshot.Threads.Count} thread(s) ({blocked} likely blocked), {snapshot.Locks.Count} SyncBlock(s) ({contended} contended). Walk {snapshot.WalkDuration.TotalMilliseconds:N0} ms. Handle `{handle.Id}` (~10 min). Use query_thread_snapshot(view=top-blocked|threads-summary|stack|lock-graph|deadlocks|unique-stacks|async-stalls|threadpool).";
            }

            if (snapshot.SnapshotKind is not "exact")
            {
                summary += $" SnapshotKind={snapshot.SnapshotKind}";
                if (snapshot.WindowSeconds is int w)
                {
                    summary += $" over {w}s window";
                }
                summary += ".";
            }
            if (snapshot.Warnings is { Count: > 0 })
            {
                summary += $" Caveats: {string.Join(" ", snapshot.Warnings.Take(3))}";
            }

            var hint = contended > 0
                ? new NextActionHint("query_snapshot",
                    "Check the captured lock graph for wait-for cycles before drilling into individual stacks.",
                    new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "deadlocks" })
                : blocked > 0
                    ? new NextActionHint("query_snapshot",
                        "Drill into the top blocked threads.",
                        new Dictionary<string, object?> { ["handle"] = handle.Id, ["view"] = "top-blocked" })
                    : null;

            var result = hint is null
                ? DiagnosticResult.Ok(summaryView, summary)
                : DiagnosticResult.Ok(summaryView, summary, hint);
            return WithContext(result, liveCtx);
        }, cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("ptrace")]
    [McpServerTool(
        Name = "capture_method_bytes",
        Title = "Capture JIT-emitted native code for a managed method",
        Destructive = false,
        ReadOnly = true,
        Idempotent = false,
        UseStructuredContent = true)]
    [Description(
        "Reads JIT-emitted machine code for a single managed method out of the runtime's " +
        "code-heap and writes it to disk as a header-less raw blob, then emits a handoff " +
        "hint to dotnet-native-mcp.disassemble(rawBlob=true). Closes the only gap left in " +
        "disasm coverage (NativeAOT and R2R are already covered on-disk by dotnet-native-mcp; " +
        "JIT-emitted code only lives in the live process / dump). Useful for diffing tier " +
        "promotion (Tier0 → Tier1+PGO) of a hot method observed via " +
        "collect_event_source(\"Microsoft-Windows-DotNETRuntime\", keywords=Jit|JitTracing). " +
        "Supply at most ONE of processId or dumpFilePath: processId attaches via ClrMD with " +
        "suspend (sub-second for a single method); dumpFilePath analyses a WithHeap/Full dump " +
        "offline. When both are omitted the server auto-selects a live .NET process. The " +
        "method is identified by its (moduleVersionId, metadataToken) handoff key — the same " +
        "key dotnet-assembly-mcp uses. Optional codeAddress (e.g. from a MethodLoad_V2 event) " +
        "is a fast-path; tier is an informational label echoed back (ClrMD does not expose " +
        "the JIT OptimizationTier directly). NativeAOT and pure ReadyToRun targets are not " +
        "supported — disassemble the on-disk binary with dotnet-native-mcp.disassemble instead.")]
    public static async Task<DiagnosticResult<CapturedMethodBytes>> CaptureMethodBytes(
        IJitMethodCapturer capturer,
        IProcessContextResolver resolver,
        [Description("PE module MVID (D format, e.g. '6f5c9bf0-1e0b-4f3b-9a8e-…') of the assembly that declares the method. Required.")] string moduleVersionId,
        [Description("IL method-def metadata token (table 0x06). Accepts decimal or hex (0x06000142). Required.")] string metadataToken,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Optional fast-path: a code address already observed for this method (e.g. MethodCodeStart from MethodLoad_V2). Hex (with or without 0x prefix) or decimal. Mismatches with (moduleVersionId, metadataToken) surface as a warning, not a hard error.")] string? codeAddress = null,
        [Description("Optional tier label echoed back on the result (e.g. 'Tier0', 'Tier1', 'Tier1OSR'). ClrMD does not expose the JIT OptimizationTier directly; this field is informational. The authoritative MethodCompilationType (None/Jit/Ngen) is always returned.")] string? tier = null,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). Defaults to 'method-bytes/{pid}'. MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). .bin files are written with POSIX mode 0600.")] string? outputDirectory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg<CapturedMethodBytes>(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg<CapturedMethodBytes>(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (string.IsNullOrWhiteSpace(metadataToken)) return InvalidArg<CapturedMethodBytes>(nameof(metadataToken), "is required");
        if (!TryParseHexOrInt(metadataToken, out var tokenValue) || tokenValue <= 0)
        {
            return InvalidArg<CapturedMethodBytes>(nameof(metadataToken), "must be a positive integer (decimal or 0x-prefixed hex)");
        }

        ulong? codeAddressValue = null;
        if (!string.IsNullOrWhiteSpace(codeAddress))
        {
            if (!TryParseUnsignedHexOrInt(codeAddress, out var addr) || addr == 0)
            {
                return InvalidArg<CapturedMethodBytes>(nameof(codeAddress), "must be a non-zero address (decimal or 0x-prefixed hex)");
            }
            codeAddressValue = addr;
        }

        var hasExplicitPid = processId.HasValue && processId.Value != 0;
        var hasDump = !string.IsNullOrWhiteSpace(dumpFilePath);
        if (hasExplicitPid && hasDump)
        {
            return InvalidArg<CapturedMethodBytes>(nameof(dumpFilePath), "processId and dumpFilePath are mutually exclusive");
        }

        int livePid = 0;
        ProcessContext? liveCtx = null;
        if (!hasDump)
        {
            var resolved = await ResolveContextAsync<CapturedMethodBytes>(resolver, processId, cancellationToken).ConfigureAwait(false);
            if (resolved.Failure is not null) return resolved.Failure;
            livePid = resolved.ProcessId;
            liveCtx = resolved.Context;
        }

        var request = new MethodCaptureRequest(
            ModuleVersionId: mvid,
            MetadataToken: tokenValue,
            CodeAddress: codeAddressValue,
            Tier: tier,
            OutputDirectory: outputDirectory);

        return await GuardAttachAsync("capture_method_bytes", hasDump ? (int?)null : livePid, async () =>
        {
            var captured = hasDump
                ? await capturer.CaptureFromDumpAsync(dumpFilePath!, request, cancellationToken).ConfigureAwait(false)
                : await capturer.CaptureLiveAsync(livePid, request, cancellationToken).ConfigureAwait(false);

            var summary = BuildCaptureSummary(captured);
            var hint = BuildDisassembleHint(captured);
            var result = hint is null
                ? DiagnosticResult.Ok(captured, summary)
                : DiagnosticResult.Ok(captured, summary, hint);
            return WithContext(result, liveCtx);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string BuildCaptureSummary(CapturedMethodBytes captured)
    {
        var typeFqn = captured.Method.TypeFullName ?? "<unknown type>";
        var methodName = captured.Method.MethodName;
        var origin = captured.Origin.ToString().ToLowerInvariant();
        if (captured.Regions.Count == 0)
        {
            return $"{origin} capture of {typeFqn}.{methodName} on pid {captured.ProcessId}: no JIT-emitted code present (method not yet JITted, abstract/extern, or ReadyToRun-only). " +
                   "Check warnings on the payload for details.";
        }
        var sizes = string.Join(", ", captured.Regions.Select(r => $"{r.Region}={r.Size:N0} B"));
        var first = captured.Regions[0];
        return $"{origin} capture of {typeFqn}.{methodName} on pid {captured.ProcessId} ({captured.Architecture}, {first.CompilationType ?? "?"}): " +
               $"{sizes}. Wrote {captured.Regions.Count} file(s) under {captured.OutputDirectory}.";
    }

    private static NextActionHint? BuildDisassembleHint(CapturedMethodBytes captured)
    {
        if (captured.Regions.Count == 0) return null;
        var hot = captured.Regions.FirstOrDefault(r => r.Region == "Hot") ?? captured.Regions[0];
        return new NextActionHint(
            "dotnet-native-mcp.disassemble",
            $"Disassemble the captured {hot.Region} region. The file is a raw blob with no PE/ELF/Mach-O header — pass rawBlob=true so the disassembler skips header validation.",
            new Dictionary<string, object?>
            {
                ["imagePath"] = hot.FilePath,
                ["rawBlob"] = true,
                ["rva"] = 0,
                ["size"] = hot.Size,
                ["architecture"] = hot.Architecture,
                ["baseAddress"] = hot.BaseAddress,
            });
    }

    [Description(
        "Streams a PE or PDB for a loaded managed module in repeated CallTool chunks so sibling MCPs can materialise pod-local binaries through the orchestrator proxy. " +
        "Resolve the module by ModuleVersionId (GUID 'D'); asset defaults to 'pe'. For PDBs the tool prefers a sibling .pdb next to the module, then falls back to an embedded portable PDB inside the PE. " +
        "processId is optional — when omitted the server auto-selects a live .NET process via the usual resolver. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetModuleBytes(
        IModuleByteSource moduleByteSource,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        [Description("PE module MVID (GUID 'D' format) of the loaded module to stream. Required.")] string moduleVersionId,
        [Description("Artifact to stream: 'pe' (default) or 'pdb'.")] string asset = "pe",
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(moduleVersionId)) return InvalidArg<ByteFetchEnvelope>(nameof(moduleVersionId), "is required");
        if (!Guid.TryParse(moduleVersionId, out var mvid)) return InvalidArg<ByteFetchEnvelope>(nameof(moduleVersionId), "must be a GUID in 'D' format");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.GetModuleBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_module_bytes",
            identifierName: "mvid",
            identifierValue: mvid.ToString("D"),
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        var resolved = await ResolveContextAsync<ByteFetchEnvelope>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;

        return await GuardAttachAsync("get_module_bytes", resolved.ProcessId, async () =>
        {
            try
            {
                var envelope = await moduleByteSource.FetchAsync(resolved.ProcessId, mvid, asset, offset, maxBytes, cancellationToken).ConfigureAwait(false);
                AuditByteFetch(logger, principalAccessor.Current, "get_module_bytes", envelope.Identifier, null, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
                var result = BuildByteFetchResult(
                    envelope,
                    BuildByteFetchSummary(envelope),
                    BuildModuleByteFetchHint(envelope, resolved.ProcessId, asset, maxBytes));
                return WithContext(result, resolved.Context);
            }
            catch (FileNotFoundException ex)
            {
                return ArtifactNotFound<ByteFetchEnvelope>("get_module_bytes", ex.Message, ex.FileName ?? mvid.ToString("D"));
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    [Description(
        "Streams a dump file under the artifact root in repeated CallTool chunks so sibling MCPs can materialise pod-local dumps through the orchestrator proxy. dumpFilePath may be relative to MCP_ARTIFACT_ROOT or absolute when it still resolves under that root after symlink resolution. " +
        "Path hints are untrusted: the tool re-validates every call through the artifact-root sandbox. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static async Task<DiagnosticResult<ByteFetchEnvelope>> GetDumpBytes(
        IDumpByteSource dumpByteSource,
        IPrincipalAccessor principalAccessor,
        [Description("Dump path to stream. Relative paths are resolved under the artifact root; absolute paths are allowed only when they still resolve under that root. Required.")] string dumpFilePath,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(dumpFilePath)) return InvalidArg<ByteFetchEnvelope>(nameof(dumpFilePath), "is required");
        if (offset < 0) return InvalidArg<ByteFetchEnvelope>(nameof(offset), "must be >= 0");
        if (maxBytes <= 0) return InvalidArg<ByteFetchEnvelope>(nameof(maxBytes), "must be > 0");

        var logger = loggerFactory?.CreateLogger("DotnetDiagnosticsMcp.Server.Tools.GetDumpBytes");
        var explicitScopeFailure = RequireLiteralScope<ByteFetchEnvelope>(
            principalAccessor,
            logger,
            "get_dump_bytes",
            identifierName: "dumpPath",
            identifierValue: dumpFilePath,
            offset);
        if (explicitScopeFailure is not null)
        {
            return explicitScopeFailure;
        }

        try
        {
            var envelope = await dumpByteSource.FetchAsync(dumpFilePath, offset, maxBytes, cancellationToken).ConfigureAwait(false);
            AuditByteFetch(logger, principalAccessor.Current, "get_dump_bytes", null, envelope.Identifier, envelope.Offset, envelope.ChunkSize, envelope.TotalSize);
            return BuildByteFetchResult(envelope, BuildByteFetchSummary(envelope), BuildDumpByteFetchHint(envelope, maxBytes));
        }
        catch (DotnetDiagnosticsMcp.Core.Artifacts.ArtifactPathException artifactEx)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_dump_bytes rejected the request: {artifactEx.Message}",
                new DiagnosticError("InvalidArtifactPath", artifactEx.Message, artifactEx.ParameterName),
                new NextActionHint("get_bytes",
                    "Re-issue with a path under the artifact root; absolute paths must still resolve under that root after symlink resolution."));
        }
        catch (FileNotFoundException ex)
        {
            return ArtifactNotFound<ByteFetchEnvelope>("get_dump_bytes", ex.Message, ex.FileName ?? dumpFilePath);
        }
        catch (InvalidOperationException ex)
        {
            return DiagnosticResult.Fail<ByteFetchEnvelope>(
                $"get_dump_bytes rejected the request: {ex.Message}",
                new DiagnosticError("InvalidArgument", ex.Message, ex.GetType().FullName));
        }
    }

    private static bool TryParseHexOrInt(string value, out int result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return int.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    private static bool TryParseUnsignedHexOrInt(string value, out ulong result)
    {
        var s = value.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.StartsWith("0X", StringComparison.Ordinal))
        {
            return ulong.TryParse(s.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out result);
        }
        return ulong.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out result);
    }

    [RequireScope("ptrace")]
    [Description(
        "Returns a slice of a thread snapshot previously captured by collect_thread_snapshot, addressed by its handle. Views: " +
        "`threads-summary` (every managed thread with state + top frame), " +
        "`stack` (full captured frames of one thread — requires `threadId`; for `linux-native-stack` snapshots this is the OS thread id / TID), " +
        "`lock-graph` (every SyncBlock that is held or contended, sorted by waiter count then recursion), " +
        "`deadlocks` (wait-for cycle detection over the captured lock graph, with lock chains and suggested SOS follow-up commands), " +
        "`top-blocked` (threads ranked by IsLikelyBlocked then LockCount — fastest path to spot contention), " +
        "`unique-stacks` (groups threads by identical top-frame signatures and returns representative canonical stacks), " +
        "`async-stalls` (best-effort classification of async-looking stacks such as Task.Result/Wait, Channel waits, TCS waits, SemaphoreSlim.WaitAsync and Task.Delay), " +
        "`threadpool` (SOS !threadpool-style snapshot of worker/IOCP counts plus global/local queue depths and pending work items when the backend captured them). " +
        "Handles expire ~10 minutes after capture; live-origin handles are invalidated when the target PID exits.")]
    public static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by collect_thread_snapshot.")] string handle,
        [Description("Which slice to return: 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls' or 'threadpool'.")] string view = "top-blocked",
        [Description("For view='stack': thread id key to return frames for. CoreCLR snapshots use ManagedThreadId; linux-native-stack snapshots use OSThreadId (TID). Ignored by other views.")] int? threadId = null,
        [Description("Maximum entries returned by ranked-list views ('threads-summary', 'top-blocked', 'lock-graph', 'unique-stacks') or the number of deadlock cycles returned by 'deadlocks'. Defaults to 50.")] int topN = 50,
        [Description("For view='unique-stacks': number of top frames folded into the signature hash. Defaults to 20. Ignored by other views.")] int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        [Description("For view='unique-stacks': drop groups with fewer than this many threads. Defaults to 1. Ignored by other views.")] int minCount = 1)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<ThreadSnapshotQueryResult>(nameof(handle), "is required");
        if (topN < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(topN), "must be >= 1");

        var snapshot = handles.TryGet<ThreadSnapshotArtifact>(handle);
        if (snapshot is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Thread snapshot handles live ~10min; live-origin handles are also invalidated when the target PID exits.", handle),
                new NextActionHint("collect_thread_snapshot", "Re-capture to issue a fresh handle.",
                    new Dictionary<string, object?> { ["processId"] = "<pid>" }));
        }

        var origin = snapshot.Origin.ToString().ToLowerInvariant();
        var normalized = view.Trim().ToLowerInvariant();
        return normalized switch
        {
            "threads-summary" => QueryThreadsSummary(snapshot, handle, origin, topN),
            "stack" => QueryThreadStack(snapshot, handle, origin, threadId),
            "lock-graph" => QueryLockGraph(snapshot, handle, origin, topN),
            "deadlocks" => QueryDeadlocks(snapshot, handle, origin, topN),
            "top-blocked" => QueryTopBlocked(snapshot, handle, origin, topN),
            "unique-stacks" => QueryUniqueStacks(snapshot, handle, origin, topN, framesToHash, minCount),
            "async-stalls" => QueryAsyncStalls(snapshot, handle, origin, topN),
            "threadpool" => QueryThreadPool(snapshot, handle, origin),
            _ => InvalidArg<ThreadSnapshotQueryResult>(nameof(view), $"must be 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls' or 'threadpool' (got '{view}')"),
        };
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadsSummary(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Threads.Take(topN).ToArray();
        var summary = $"Returning {ordered.Length}/{snapshot.Threads.Count} thread(s) from snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "threads-summary", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Threads = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadStack(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int? threadId)
    {
        if (threadId is null)
        {
            return InvalidArg<ThreadSnapshotQueryResult>(nameof(threadId), "is required for view='stack'");
        }
        var isLinuxNativeStack = string.Equals(snapshot.Source, "linux-native-stack", StringComparison.Ordinal);
        var thread = isLinuxNativeStack
            ? snapshot.Threads.FirstOrDefault(t =>
                threadId.Value > 0 &&
                (uint)threadId.Value == t.OSThreadId)
            : snapshot.Threads.FirstOrDefault(t => t.ManagedThreadId == threadId.Value);
        if (thread is null)
        {
            var threadKind = isLinuxNativeStack ? "OS thread" : "managed thread";
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"{threadKind} {threadId.Value} not present in snapshot '{handle}'.",
                new DiagnosticError("ThreadNotFound", "The captured snapshot does not contain this thread id.", threadId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new NextActionHint("query_snapshot",
                    "List the captured threads first.",
                    new Dictionary<string, object?> { ["handle"] = handle, ["view"] = "threads-summary" }));
        }
        var selectedId = isLinuxNativeStack ? threadId.Value : thread.ManagedThreadId;
        var threadLabel = isLinuxNativeStack ? "OS thread" : "managed thread";
        var summary = $"Stack of {threadLabel} {selectedId} (OS {thread.OSThreadId}, state {thread.State}) from snapshot '{handle}' — {thread.Frames.Count} frame(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "stack", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                Thread = thread,
                ThreadId = selectedId,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryLockGraph(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Locks.Take(topN).ToArray();
        var summary = ordered.Length == 0
            ? $"Snapshot '{handle}' contains no held or contended SyncBlocks."
            : $"Returning {ordered.Length}/{snapshot.Locks.Count} SyncBlock(s) from snapshot '{handle}'. Most contended: object 0x{ordered[0].ObjectAddress:x} ({ordered[0].ObjectTypeFullName ?? "<unknown>"}) — {ordered[0].WaitingThreadCount} waiter(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "lock-graph", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Locks = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryDeadlocks(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var deadlocks = ThreadDeadlockDetector.Detect(snapshot, handle, topN);
        var edgeCount = snapshot.Locks.Sum(lockState => lockState.WaitingManagedThreadIds.Count(waiterId => waiterId > 0 && waiterId != lockState.OwnerManagedThreadId));
        var summary = deadlocks.Count == 0
            ? $"No deadlock cycles detected in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) across {edgeCount} waiter→owner edge(s)."
            : $"Detected {deadlocks.Count} deadlock cycle(s) in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId}) across {edgeCount} waiter→owner edge(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "deadlocks", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Deadlocks = deadlocks },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryTopBlocked(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var ordered = snapshot.Threads
            .OrderByDescending(t => t.IsLikelyBlocked)
            .ThenByDescending(t => t.LockCount)
            .ThenByDescending(t => t.Frames.Count)
            .Take(topN)
            .ToArray();
        var blocked = ordered.Count(t => t.IsLikelyBlocked);
        var summary = $"Returning {ordered.Length} thread(s) from snapshot '{handle}' ranked by likely-blocked then LockCount — {blocked} flagged as likely blocked.";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "top-blocked", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration) { Threads = ordered },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryUniqueStacks(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN, int framesToHash, int minCount)
    {
        if (framesToHash < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(framesToHash), "must be >= 1");
        if (minCount < 1) return InvalidArg<ThreadSnapshotQueryResult>(nameof(minCount), "must be >= 1");

        var allGroups = ThreadSnapshotUniqueStackGrouper.Group(snapshot.Threads, framesToHash, minCount, int.MaxValue);
        var pagedGroups = allGroups.Take(topN).ToArray();
        var summary = pagedGroups.Length == 0
            ? $"Snapshot '{handle}' has no unique stack groups with at least {minCount} thread(s)."
            : $"Returning {pagedGroups.Length}/{allGroups.Count} unique stack group(s) from snapshot '{handle}' hashed over the top {framesToHash} frame(s). Largest group: {pagedGroups[0].ThreadCount}/{snapshot.Threads.Count} thread(s).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "unique-stacks", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                UniqueStacks = pagedGroups,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryAsyncStalls(
        ThreadSnapshotArtifact snapshot, string handle, string origin, int topN)
    {
        var view = AsyncStallClassifier.Classify(snapshot, topN);
        var summary = view.ClassifiedThreads == 0
            ? $"No async-looking parked continuations detected in snapshot '{handle}' ({origin}, pid {snapshot.ProcessId})."
            : $"Classified {view.ClassifiedThreads} async-looking thread(s) from snapshot '{handle}'. Top bucket: {view.ByBucket[0].Bucket} ({view.ByBucket[0].Count}).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "async-stalls", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                AsyncStalls = view,
            },
            summary);
    }

    private static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadPool(
        ThreadSnapshotArtifact snapshot, string handle, string origin)
    {
        if (snapshot.ThreadPool is null)
        {
            return DiagnosticResult.Fail<ThreadSnapshotQueryResult>(
                $"Snapshot '{handle}' does not contain ThreadPool counters/queues.",
                new DiagnosticError("ViewNotCaptured", "Re-run collect_thread_snapshot on a ClrMD-backed CoreCLR target; fallback backends may not capture ThreadPool internals.", handle));
        }

        var threadPool = snapshot.ThreadPool;
        var summary = $"ThreadPool from snapshot '{handle}': workers {threadPool.Workers.Current} current ({threadPool.Workers.Active} active, {threadPool.Workers.Idle} idle, min {threadPool.Workers.Min}, max {threadPool.Workers.Max}), IOCP {threadPool.Iocp.Current} current (idle {threadPool.Iocp.Idle}, min {threadPool.Iocp.Min}, max {threadPool.Iocp.Max}), pending work items {threadPool.PendingWorkItems} (global {threadPool.Queues.GlobalQueueLength}, local {threadPool.Queues.LocalQueues.Sum(q => q.QueueLength)} across {threadPool.Queues.LocalQueues.Count} local queue(s)).";
        return DiagnosticResult.Ok(
            new ThreadSnapshotQueryResult(handle, "threadpool", origin, snapshot.ProcessId, snapshot.CapturedAt, snapshot.WalkDuration)
            {
                ThreadPool = threadPool,
            },
            summary);
    }


    [RequireScope("investigation-export")]
    [McpServerTool(
        Name = "start_investigation",
        Title = "Plan a .NET performance investigation (decision tree)",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Use this to PLAN a non-trivial .NET performance investigation — a slow app, high latency, high CPU, " +
        "or growing/leaking memory — when you want a decision tree before collecting. " +
        "Returns a structured InvestigationPlan: a ready-to-execute decision tree of tool calls with " +
        "rationale, decision branches, early-stop conditions, and constraints (MaxToolCalls, " +
        "dump-requires-approval). After it returns, EXECUTE the first recommended tool call — do not just " +
        "summarize the plan back to the user. For a quick one-shot first look instead, call " +
        "inspect_process(view=\"triage\"). " +
        "Modes are resolved from the arguments: hypothesis present → " +
        "'hypothesis' (routes directly to the relevant evidence collector); baseline present → " +
        "'warm' (skips covered steps, emits MetricComparisons against baseline); otherwise 'cold' " +
        "(USE-style: snapshot_counters first, branch on evidence). Call this BEFORE any other " +
        "collector when the symptom is non-trivial — it pays for itself by preventing loops.")]
    public static async Task<DiagnosticResult<InvestigationPlan>> StartInvestigation(
        IInvestigationPlanner planner,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Plain-language symptom, e.g. 'high latency on /checkout since v2025.10'. Required for cold mode; optional for warm/hypothesis.")] string? symptom = null,
        [Description("Specific hypothesis to test, e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.")] string? hypothesis = null,
        [Description("Baseline snapshot from a prior investigation (JSON of BaselineHandle). Triggers warm mode.")] BaselineHandle? baseline = null,
        [Description("Optional hard limit on tool calls before forcing summarization. Defaults to 8.")] int maxToolCalls = 8,
        [Description("If true, collect_process_dump steps are marked approval-gated. Defaults to true.")] bool dumpRequiresApproval = true,
        CancellationToken cancellationToken = default)
    {
        if (maxToolCalls < 1) return InvalidArg<InvestigationPlan>(nameof(maxToolCalls), "must be >= 1");

        var resolved = await ResolveContextAsync<InvestigationPlan>(resolver, processId, cancellationToken).ConfigureAwait(false);
        if (resolved.Failure is not null) return resolved.Failure;
        var pid = resolved.ProcessId;

        var request = new InvestigationRequest(
            ProcessId: pid,
            Symptom: symptom,
            Hypothesis: hypothesis,
            Baseline: baseline,
            Constraints: new InvestigationConstraints(
                MaxToolCalls: maxToolCalls,
                DumpRequiresApproval: dumpRequiresApproval));

        var plan = planner.Plan(request);
        var summary = $"Mode={plan.Mode}. Next step #{plan.NextStep.StepNumber}: {plan.NextStep.ToolName}. " +
                      $"{plan.AllSteps.Count} total step(s), {plan.EarlyStopConditions.Count} early-stop condition(s). " +
                      $"Honor MaxToolCalls={plan.Constraints.MaxToolCalls}.";

        var hintParams = new Dictionary<string, object?>(plan.NextStep.ToolParams);
        return WithContext(DiagnosticResult.Ok(
            plan,
            summary,
            new NextActionHint(plan.NextStep.ToolName, plan.NextStep.Rationale, hintParams)),
            resolved.Context);
    }

    [RequireScope("investigation-export")]
    [McpServerTool(
        Name = "export_investigation_summary",
        Title = "Export portable investigation summary",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reads a prior collect_cpu_sample drill-down handle and produces a portable, versioned " +
        "InvestigationSummary (~5-20 KB JSON) ready to paste into a PR, ADR, or ticket. " +
        "Includes build + container provenance harvested from the sidecar environment, stable " +
        "module+methodFullName symbol refs (survive rebuilds where line numbers shift), and " +
        "optional lineage to a previous investigation. Set `format=markdown` for a human-readable " +
        "version. The server is stateless: the LLM owns persistence — paste the JSON into a doc " +
        "and feed it back via `compare_to_baseline` on the next deploy.")]
    public static DiagnosticResult<ExportedInvestigationSummary> ExportInvestigationSummary(
        IInvestigationSummaryExporter exporter,
        IDiagnosticHandleStore handles,
        [Description("Handle returned by a prior collect_cpu_sample call.")] string handle,
        [Description("Output format: 'json' (default — portable, machine-readable) or 'markdown' (human-readable for PRs).")] SummaryFormat format = SummaryFormat.Json,
        [Description("Max hotspots to include in the summary. Defaults to 10.")] int topHotspots = 10,
        [Description("Optional managed assembly name for the target (from list_dotnet_processes).")] string? buildAssemblyName = null,
        [Description("Optional investigation id from the previous summary, to link lineage.")] string? previousInvestigationId = null,
        [Description("Optional commit SHA being proposed as the fix.")] string? fixCommitSha = null,
        [Description("Optional PR URL being proposed as the fix.")] string? fixPullRequestUrl = null,
        [Description("Optional short description of the proposed fix.")] string? fixDescription = null,
        [Description("Optional free-form notes appended to the summary.")] string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(handle)) return InvalidArg<ExportedInvestigationSummary>(nameof(handle), "is required");
        if (topHotspots < 1) return InvalidArg<ExportedInvestigationSummary>(nameof(topHotspots), "must be >= 1");

        var artifact = handles.TryGet<CpuSampleTraceArtifact>(handle);
        if (artifact is null)
        {
            return DiagnosticResult.Fail<ExportedInvestigationSummary>(
                $"Handle '{handle}' is unknown or expired.",
                new DiagnosticError("HandleExpired", "Drill-down handles live ~10min and are invalidated when the target process exits.", handle),
                new NextActionHint("collect_sample", "Re-run the sampler on the same pid to issue a fresh handle.",
                    new Dictionary<string, object?> { ["durationSeconds"] = 10 }));
        }

        var fix = (fixCommitSha is null && fixPullRequestUrl is null && fixDescription is null)
            ? null
            : new InvestigationFixTarget(fixCommitSha, fixPullRequestUrl, fixDescription);

        var exported = exporter.Export(new ExportRequest(
            Handle: handle,
            Artifact: artifact,
            TopHotspots: topHotspots,
            BuildAssemblyName: buildAssemblyName,
            PreviousInvestigationId: previousInvestigationId,
            TargetsFix: fix,
            Notes: notes,
            Format: format));

        var bytes = exported.Rendered.Length;
        return DiagnosticResult.Ok(
            exported,
            $"Exported investigation {exported.Summary.InvestigationId} ({exported.Summary.Findings.TopHotspots.Count} hotspots, {bytes} chars {format}). Paste `rendered` into your PR/ADR; re-supply this JSON via compare_to_baseline on the next investigation.",
            new NextActionHint("compare_to_baseline", "When you investigate the next deploy, pass this summary as the baseline.",
                new Dictionary<string, object?> { ["baselineSummaryJson"] = "<paste rendered JSON here>" }));
    }

    [RequireScope("investigation-export")]
    [McpServerTool(
        Name = "compare_to_baseline",
        Title = "Compare investigation summary to baseline",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Diffs two InvestigationSummary JSON documents (produced by export_investigation_summary) " +
        "and returns a SummaryDiff: provenance delta (image jump, git sha change), new hotspots " +
        "(strongest regression signal — symbol not present in baseline), removed hotspots " +
        "(improvements), and changed hotspots above a ±2 percentage-point threshold. The verdict " +
        "field collapses the diff into one of: no_regression, no_regression_after_deploy, " +
        "regression_new_hotspot, regression_increased_hotspot, improvement.")]
    public static DiagnosticResult<SummaryDiff> CompareToBaseline(
        ISummaryComparer comparer,
        [Description("Baseline summary JSON (from a prior export_investigation_summary).")] string baselineSummaryJson,
        [Description("Current summary JSON (from export_investigation_summary on the new investigation).")] string currentSummaryJson)
    {
        if (string.IsNullOrWhiteSpace(baselineSummaryJson)) return InvalidArg<SummaryDiff>(nameof(baselineSummaryJson), "is required");
        if (string.IsNullOrWhiteSpace(currentSummaryJson)) return InvalidArg<SummaryDiff>(nameof(currentSummaryJson), "is required");

        InvestigationSummary baseline, current;
        try
        {
            baseline = System.Text.Json.JsonSerializer.Deserialize(
                    baselineSummaryJson,
                    DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryJsonContext.Default.InvestigationSummary)
                ?? throw new InvalidOperationException("baseline summary deserialized to null");
            current = System.Text.Json.JsonSerializer.Deserialize(
                    currentSummaryJson,
                    DotnetDiagnosticsMcp.Core.Memory.InvestigationSummaryJsonContext.Default.InvestigationSummary)
                ?? throw new InvalidOperationException("current summary deserialized to null");
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            return DiagnosticResult.Fail<SummaryDiff>(
                "Could not parse one of the supplied summary JSON documents.",
                new DiagnosticError("InvalidSummaryJson", ex.Message),
                new NextActionHint("export_investigation_summary", "Re-export the baseline and current summaries and try again."));
        }

        if (!string.Equals(baseline.Schema, InvestigationSummary.SchemaV1, StringComparison.Ordinal) ||
            !string.Equals(current.Schema, InvestigationSummary.SchemaV1, StringComparison.Ordinal))
        {
            return DiagnosticResult.Fail<SummaryDiff>(
                $"Unsupported schema. Expected '{InvestigationSummary.SchemaV1}'.",
                new DiagnosticError("UnsupportedSchema", $"baseline='{baseline.Schema}' current='{current.Schema}'"),
                new NextActionHint("export_investigation_summary", "Re-export both summaries with the current server version."));
        }

        if (baseline.Provenance is null || baseline.Findings is null || baseline.Findings.TopHotspots is null ||
            current.Provenance is null || current.Findings is null || current.Findings.TopHotspots is null)
        {
            return DiagnosticResult.Fail<SummaryDiff>(
                "Summary JSON is missing required fields (Provenance / Findings / Findings.TopHotspots).",
                new DiagnosticError("InvalidSummaryJson", "Required fields are null after deserialization."),
                new NextActionHint("export_investigation_summary", "Re-export the summaries from a fresh investigation."));
        }

        var diff = comparer.Compare(baseline, current);
        var summaryLine = $"Verdict: {diff.Verdict}. {diff.NewHotspots.Count} new, " +
                          $"{diff.RemovedHotspots.Count} removed, {diff.ChangedHotspots.Count} changed hotspots. " +
                          $"Provenance: {diff.Provenance.Summary}.";

        return DiagnosticResult.Ok(diff, summaryLine,
            new NextActionHint("collect_sample",
                diff.Verdict.StartsWith("regression", StringComparison.Ordinal)
                    ? "Re-sample the regressing process and drill into the new top frame."
                    : "Optional: capture a fresh sample to confirm the improvement is stable.",
                new Dictionary<string, object?> { ["durationSeconds"] = 20 }));
    }


    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    private static DiagnosticResult<T>? RequireLiteralScope<T>(
        IPrincipalAccessor principalAccessor,
        Microsoft.Extensions.Logging.ILogger? logger,
        string tool,
        string identifierName,
        string identifierValue,
        long offset)
    {
        var principal = principalAccessor.Current;
        if (principal?.HasExplicitScope("module-bytes-read") == true)
        {
            return null;
        }

        logger?.LogWarning(
            "{Tool} denied: explicit module-bytes-read scope required. tokenName={TokenName} {IdentifierName}={IdentifierValue} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
            tool,
            principal?.Name ?? "(none)",
            identifierName,
            identifierValue,
            offset,
            0,
            0);

        return DiagnosticResult.Fail<T>(
            $"{tool} requires the literal scope 'module-bytes-read'. Root or wildcard tokens do not auto-grant this modifier scope.",
            new DiagnosticError(
                "Forbidden",
                "Grant the bearer principal the literal scope 'module-bytes-read'. Root ('*') is intentionally insufficient for this modifier scope.",
                principal?.Name),
            new NextActionHint(
                tool,
                "Retry with a bearer token that explicitly includes 'module-bytes-read'."));
    }

    private static void AuditByteFetch(
        Microsoft.Extensions.Logging.ILogger? logger,
        BearerPrincipal? principal,
        string tool,
        string? mvid,
        string? dumpPath,
        long offset,
        int chunkSize,
        long totalSize)
    {
        if (logger is null)
        {
            return;
        }

        if (mvid is not null)
        {
            logger.LogInformation(
                "{Tool} streamed bytes. tokenName={TokenName} mvid={Mvid} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
                tool,
                principal?.Name ?? "(none)",
                mvid,
                offset,
                chunkSize,
                totalSize);
            return;
        }

        logger.LogInformation(
            "{Tool} streamed bytes. tokenName={TokenName} dumpPath={DumpPath} offset={Offset} chunkSize={ChunkSize} totalSize={TotalSize}",
            tool,
            principal?.Name ?? "(none)",
            dumpPath ?? "(none)",
            offset,
            chunkSize,
            totalSize);
    }

    private static DiagnosticResult<ByteFetchEnvelope> BuildByteFetchResult(
        ByteFetchEnvelope envelope,
        string summary,
        NextActionHint? hint)
        => hint is null
            ? DiagnosticResult.Ok(envelope, summary)
            : DiagnosticResult.Ok(envelope, summary, hint);

    private static string BuildByteFetchSummary(ByteFetchEnvelope envelope)
    {
        var streamed = envelope.ChunkSize == 0
            ? $"No bytes remain at offset {envelope.Offset:N0}"
            : $"Streamed {envelope.ChunkSize:N0} byte(s) from offset {envelope.Offset:N0}";
        var more = envelope.NextOffset is long next
            ? $" Next chunk starts at offset {next:N0}."
            : " Stream complete.";
        return $"{streamed} of {envelope.TotalSize:N0} total byte(s) for {envelope.Kind} {envelope.Identifier} ({envelope.Asset}).{more}";
    }

    private static NextActionHint? BuildModuleByteFetchHint(ByteFetchEnvelope envelope, int processId, string asset, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same module asset.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "module",
                    ["moduleVersionId"] = envelope.Identifier,
                    ["asset"] = string.IsNullOrWhiteSpace(asset) ? envelope.Asset : asset,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                    ["processId"] = processId,
                })
            : null;

    private static NextActionHint? BuildDumpByteFetchHint(ByteFetchEnvelope envelope, int maxBytes)
        => envelope.NextOffset is long next
            ? new NextActionHint(
                "get_bytes",
                "Continue streaming the next chunk from the same dump artifact.",
                new Dictionary<string, object?>
                {
                    ["kind"] = "dump",
                    ["dumpFilePath"] = envelope.Identifier,
                    ["offset"] = next,
                    ["maxBytes"] = maxBytes,
                })
            : null;

    private static DiagnosticResult<T> ArtifactNotFound<T>(string tool, string message, string detail)
        => DiagnosticResult.Fail<T>(
            $"{tool} could not locate the requested artifact: {message}",
            new DiagnosticError("ArtifactNotFound", message, detail));

    /// <summary>
    /// B4 / issue #165 / M3 helper: returns a denial envelope when the caller-supplied
    /// <paramref name="symbolPath"/> references a remote symbol-server host that is not on
    /// the configured allowlist. Returns <c>null</c> when the path is allowed (local path,
    /// empty/null, or remote host on the allowlist) so the caller can early-return only on
    /// denial. Must be invoked from every tool that forwards a caller-supplied
    /// <c>symbolPath</c> into a SymbolReader / native symbolicator backend. B5.2 layers a
    /// principal-side modifier scope on top: callers holding <c>symbols-remote</c>
    /// (RFC 0001 §2.5) bypass the allowlist entirely. The legacy server-wide allowlist
    /// keeps working byte-for-byte for principals without the scope.
    ///
    /// B5.4 / RFC 0001 §7.3: when the allowlist (not the scope) was the path that allowed
    /// a remote host through, fires a once-per-process deprecation warning via
    /// <paramref name="deprecation"/>. The allowlist policy itself is retained — only the
    /// pattern of relying on a single deployment-wide allowlist for caller-level
    /// distinction is deprecated.
    /// </summary>
    // Symbol-path validation lives in Core (UseCases/SymbolPathValidation) since #288 so the CLI and
    // the MCP tools share one source of truth. This thin forward hoists the transport-specific bypass
    // decision (the RFC 0001 `symbols-remote` scope) into a precomputed bool and adapts the Server's
    // LegacyDiagnosticsFlagDeprecation singleton onto the host-neutral ISymbolServerDeprecationSink.
    private static DiagnosticResult<T>? ValidateSymbolPath<T>(
        SymbolServerAllowlist allowlist,
        string? symbolPath,
        IPrincipalAccessor? principalAccessor = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null)
        => SymbolPathValidation.Validate<T>(
            allowlist,
            symbolPath,
            principalAccessor?.Current?.HasExplicitScope("symbols-remote") == true,
            deprecation);

    // Attach-failure classification lives in Core (UseCases/AttachGuard) since #288. These thin
    // forwards keep every existing call site (heap/dump/threads/bytes/sampling) unchanged while the
    // CLI shares the same structured envelopes.
    private static Task<DiagnosticResult<T>> GuardAttachAsync<T>(
        string tool,
        int? processId,
        Func<Task<DiagnosticResult<T>>> body,
        CancellationToken cancellationToken)
        => AttachGuard.GuardAttachAsync(tool, processId, body, cancellationToken);

    private static DiagnosticResult<T> ClassifyAttachFailure<T>(string tool, int? processId, Exception ex)
        => AttachGuard.ClassifyAttachFailure<T>(tool, processId, ex);
}
