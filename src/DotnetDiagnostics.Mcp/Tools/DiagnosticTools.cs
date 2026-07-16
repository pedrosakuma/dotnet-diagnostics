using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using DotnetDiagnostics.Core;
using DotnetDiagnostics.Core.Activities;
using DotnetDiagnostics.Core.Bytes;
using DotnetDiagnostics.Core.Capabilities;
using DotnetDiagnostics.Core.Collection;
using DotnetDiagnostics.Core.Comparison;
using DotnetDiagnostics.Core.Contention;
using DotnetDiagnostics.Core.Container;
using DotnetDiagnostics.Core.Counters;
using DotnetDiagnostics.Core.CpuSampling;
using DotnetDiagnostics.Core.Signals;
using DotnetDiagnostics.Core.Db;
using DotnetDiagnostics.Core.Networking;
using DotnetDiagnostics.Core.Dump;
using DotnetDiagnostics.Core.EventSources;
using DotnetDiagnostics.Core.Exceptions;
using DotnetDiagnostics.Core.Gc;
using DotnetDiagnostics.Core.Logs;
using DotnetDiagnostics.Core.Drilldown;
using DotnetDiagnostics.Core.Investigation;
using DotnetDiagnostics.Core.Jit;
using DotnetDiagnostics.Core.JitCapture;
using DotnetDiagnostics.Core.Kestrel;
using DotnetDiagnostics.Core.Requests;
using DotnetDiagnostics.Core.Memory;
using DotnetDiagnostics.Core.NativeAlloc;
using DotnetDiagnostics.Core.OffCpu;
using DotnetDiagnostics.Core.Preflight;
using DotnetDiagnostics.Core.ProcessDiscovery;
using DotnetDiagnostics.Core.Security;
using DotnetDiagnostics.Core.Startup;
using DotnetDiagnostics.Core.ThreadPool;
using DotnetDiagnostics.Core.Threads;
using DotnetDiagnostics.Core.Triage;
using DotnetDiagnostics.Core.UseCases;
using DotnetDiagnostics.Mcp.Security;
using DotnetDiagnostics.Mcp.Diagnostics;
using Microsoft.Diagnostics.NETCore.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using static DotnetDiagnostics.Core.UseCases.ProcessResolutionHelpers;

namespace DotnetDiagnostics.Mcp.Tools;

/// <summary>
/// MCP tools that expose the dotnet-diagnostics-mcp Core diagnostic primitives.
/// Every tool returns a <see cref="DiagnosticResult{T}"/> envelope carrying a short summary,
/// next-action hints, and the typed payload — so a low-context LLM can drill down without
/// re-reading the server instructions on every turn.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticTools
{
    [RequireScope("read-counters")]
    [Description(
        "Lists all .NET processes on the local machine that expose a Diagnostic IPC endpoint. " +
        "Returns process id, runtime version, OS, architecture and the managed entrypoint assembly. " +
        "Usually the first tool to call in any investigation.")]
    public static DiagnosticResult<IReadOnlyList<DotnetProcess>> ListDotnetProcesses(IProcessDiscovery discovery)
        => DiagnosticToolProcessInspection.ListDotnetProcesses(discovery);

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
        => DiagnosticToolProcessInspection.GetProcessInfo(discovery, resolver, processId, cancellationToken);

    [RequireScope("read-counters")]
    [Description(
        "Probes the target process to determine which diagnostic tools the server can use against it. " +
        "Detects CoreCLR vs NativeAOT (NativeAOT lacks managed CPU sampling and gcdump) and returns a capability matrix. " +
        "Takes up to ~2 seconds while probing the SampleProfiler provider. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-resolves it. " +
        "Most callers no longer need this tool first — every other tool already attaches a compact capability digest " +
        "on its response envelope, so call this explicitly only when you need the full matrix.")]
    public static Task<DiagnosticResult<DiagnosticCapabilities>> GetDiagnosticCapabilities(
        ICapabilityDetector detector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.GetDiagnosticCapabilities(detector, resolver, processId, cancellationToken);

    /// <summary>
    /// Target-optional environment self-diagnosis. Unlike <see cref="GetDiagnosticCapabilities"/>
    /// (per-target boolean matrix), this returns remediation-first findings and works with no
    /// <c>processId</c> at all — diagnosing the sidecar host before any target exists. Never fails:
    /// every finding is reported as a check, not an error envelope.
    /// </summary>
    public static DiagnosticResult<PreflightReport> PerformPreflight(
        IPreflightInspector inspector,
        int? processId = null)
        => DiagnosticToolProcessInspection.PerformPreflight(inspector, processId);

    [RequireScope("read-counters")]
    [Description(
        "Reads Linux cgroup v2 files for the target process: cpu.stat (throttling), cpu.max (quota), " +
        "memory.current / memory.max / memory.events (OOM kills), cpu/memory/io.pressure (PSI), pids and " +
        "oom_score. Closes the #1 K8s blind spot — 'app is slow but runtime CPU counters look fine' is usually " +
        "CPU throttling at the cgroup level, completely invisible from the runtime. " +
        "Cheap (file reads only, no privilege, no EventPipe session). Returns partial signals + Notes on " +
        "non-Linux hosts, cgroup v1 hosts and old kernels lacking PSI. " +
        "processId is optional: when exactly one .NET process is reachable the server auto-resolves it.")]
    public static Task<DiagnosticResult<ContainerSignals>> GetContainerSignals(
        IContainerSignalsCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops the verbose Notes (caveats about cgroup v1, missing PSI, etc.) and keeps only the actionable signals. 'detail' / 'raw' include all Notes.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.GetContainerSignals(collector, resolver, processId, depth, cancellationToken);

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
    public static Task<DiagnosticResult<MemoryTrend>> GetMemoryTrend(
        IMemoryTrendCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. When provided, any OS process is accepted (no .NET IPC required). Optional — omit to auto-select the lone reachable .NET process.")] int? processId = null,
        [Description("Duration of the observation window in seconds. Must be >= 2. Defaults to 10.")] int durationSeconds = 10,
        [Description("Interval between consecutive samples in seconds. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.GetMemoryTrend(collector, resolver, processId, durationSeconds, sampleEverySeconds, cancellationToken);

    [RequireScope("read-counters")]
    [Description(
        "Reads a managed process's runtime configuration: best-effort GC and ThreadPool settings from a short ClrMD attach, tiered-compilation overrides from filtered runtime env vars, and appContextSwitches parsed offline from the target's <app>.runtimeconfig.json (runtimeOptions.configProperties — AppContext switches and runtime knobs). " +
        "Environment variables are filtered to the DOTNET_ / COMPlus_ / ASPNETCORE_ / DOTNET_SYSTEM_ prefixes as a hard security boundary. " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
    public static Task<DiagnosticResult<RuntimeConfigView>> GetRuntimeConfig(
        IRuntimeConfigInspector inspector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.GetRuntimeConfig(inspector, resolver, processId, cancellationToken);

    [RequireScope("read-counters")]
    [Description(
        "Fast triage: collects counters (5s), reports threshold-backed observed signals separately from evidence-backed hypotheses, " +
        "and returns neutral drill-down hints. Every hypothesis includes confidence, supporting/contradicting evidence, and a next step. " +
        "Low CPU plus a small ThreadPool queue is inconclusive, not proof of I/O. " +
        "The legacy verdict and secondaryVerdicts fields remain serialized for compatibility and are deprecated for removal in v1.0.")]
    public static Task<DiagnosticResult<TriageResult>> PerformTriage(
        ICounterCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the counter collection window in seconds. Must be >= 1. Defaults to 5.")] int durationSeconds = 5,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.PerformTriage(collector, resolver, processId, durationSeconds, cancellationToken);

    [RequireScope("read-counters")]
    [Description(
        "Inspects OS-level FD / handle / socket state for a process. On Linux it reads /proc/<pid>/fd, /proc/<pid>/net/tcp{,6} and /proc/<pid>/limits to classify descriptors, aggregate TCP states and compute the nofile usage fraction. " +
        "On Windows it queries GetProcessHandleCount and reports a note that per-handle and per-socket breakdown is not yet implemented. " +
        "durationSeconds=0 returns a single snapshot (default); durationSeconds >= 2 samples a short trend window. " +
        "When processId is provided it is used directly as the OS pid (no .NET IPC check). " +
        "When processId is omitted the server auto-selects the lone reachable .NET process.")]
    public static Task<DiagnosticResult<ProcessResources>> GetProcessResources(
        IProcessResourcesCollector collector,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target process. When provided, any OS process is accepted (no .NET IPC required). Optional — omit to auto-select the lone reachable .NET process.")] int? processId = null,
        [Description("Observation window length in seconds. 0 returns a single snapshot (default); values >= 2 enable trend mode.")] int durationSeconds = 0,
        [Description("Interval between consecutive samples in seconds when trend mode is enabled. Must be >= 1. Defaults to 2.")] int sampleEverySeconds = 2,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.GetProcessResources(collector, resolver, processId, durationSeconds, sampleEverySeconds, cancellationToken);

    public static Task<DiagnosticResult<RequestsNowSnapshot>> GetRequestsNow(
        IRequestsNowCollector collector,
        IProcessContextResolver resolver,
        int? processId = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolProcessInspection.GetRequestsNow(collector, resolver, processId, cancellationToken);

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
        [Description("NativeAOT only. Filesystem path to the ILC '*.map.xml' map file produced by publishing with <IlcGenerateMapFile>true</IlcGenerateMapFile> (ilc --map). When supplied, the perf-based AOT sampler emits a name-based MethodIdentity (TypeFullName + MethodName; MVID/metadata token stay null) for hot managed methods so the dotnet-native-mcp 'disassemble this hot AOT function' handoff works. Ignored on CoreCLR. The path is a hint only — the consumer must verify the artifact before loading it.")] string? nativeAotMapFile = null,
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns the top-3 hotspots inline. 'detail' returns the requested topN (default 25). 'raw' is equivalent to detail. The full sample is always retained behind the issued handle — drill in with get_call_tree.")]
        SamplingDepth depth = SamplingDepth.Summary,
        [Description("If true, persists the raw .nettrace under the artifact root and returns its relative path so it can be fetched with get_bytes(kind='trace') for offline PerfView/Speedscope/Perfetto analysis. Defaults to false (the trace is parsed then deleted).")] bool exportTrace = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
        => await DiagnosticToolSampling.CollectCpuSample(
            sampler,
            handles,
            resolver,
            symbolServerAllowlist,
            principalAccessor,
            processId,
            durationSeconds,
            topN,
            resolveSourceLines,
            symbolPath,
            maxResolvedSources,
            resolveMethodInstantiations,
            maxResolvedMethodInstantiations,
            nativeAotMapFile,
            depth,
            exportTrace,
            deprecation,
            requestContext,
            cancellationToken).ConfigureAwait(false);

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
        => await DiagnosticToolSampling.CollectAllocationSample(
            sampler,
            handles,
            resolver,
            processId,
            durationSeconds,
            topN,
            cancellationToken).ConfigureAwait(false);

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
        => DiagnosticToolSampling.GetCallTree(handles, handle, rootMethodFilter, maxDepth, maxNodes);

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
        => await DiagnosticToolSampling.CollectOffCpuSample(
            sampler,
            handles,
            resolver,
            symbolServerAllowlist,
            principalAccessor,
            processId,
            durationSeconds,
            topN,
            symbolPath,
            depth,
            deprecation,
            cancellationToken).ConfigureAwait(false);

    /// <summary>Canonical kind tag for handles backing an <see cref="OffCpuSnapshotArtifact"/>.</summary>
    public const string OffCpuHandleKind = DiagnosticToolSampling.OffCpuHandleKind;

    /// <summary>Canonical kind tag for handles backing a native-allocation call tree
    /// (a <see cref="CpuSampleTraceArtifact"/>); walked with <c>query_snapshot(view="call-tree")</c>.</summary>
    public const string NativeAllocHandleKind = DiagnosticToolSampling.NativeAllocHandleKind;

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
        "Backend: Linux uses perf uprobes on libc malloc/calloc/realloc (needs the perf binary plus " +
        "permission to create a uprobe — CAP_SYS_ADMIN / tracefs write access). Windows uses the NT " +
        "Kernel Logger VirtualAlloc ETW provider with stack walk (needs administrative elevation / " +
        "SeSystemProfilePrivilege); both emit the identical call-tree handle. " +
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
        => await DiagnosticToolSampling.CollectNativeAllocSample(
            sampler,
            handles,
            resolver,
            processId,
            durationSeconds,
            topN,
            samplePeriod,
            cancellationToken).ConfigureAwait(false);


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
        => DiagnosticToolSampling.QueryOffCpuSnapshot(handles, handle, view, topN, stackRank);

    [RequireAnyScope("read-counters", "eventpipe")]
    [Description(
        "Re-projects a previously-collected counter/exception/crash-guard/GC/event-catalog/EventSource/Activity/log/JIT/ThreadPool/contention/db/kestrel/networking/startup artifact under a " +
        "named view, without re-running the underlying EventPipe session. Use the `handle` " +
        "returned by collect_events with kind one of counters/exceptions/crash-guard/gc/datas/catalog/event_source/activities/logs/jit/threadpool/contention/db/kestrel/networking/startup. " +
        "Supported views per kind: counters → summary|byProvider; exception-snapshot → " +
        "summary|byType|recent; crash-guard-snapshot → summary|exceptions|stack; gc-events → summary|events|pauseHistogram|timeline|longestPauses|byGeneration; event-catalog → catalog|byProvider|events; event-source → " +
        "summary|byEventName|events; activities → summary|bySource|byOperation|activities|gc-overlay; " +
        "log-snapshot → summary|byCategory|byLevel|recent|errors; jit-snapshot → summary|topMethods|tierDistribution|reJIT; threadpool-snapshot → summary|timeline|hillClimbing|workItemOrigins; contention-snapshot → summary|byCallSite|byOwner; db-snapshot → summary|byCommand|n+1|connectionPool; kestrel-snapshot → summary|byOperation|queues|tls|config; networking-snapshot → summary|byOperation|queue|tls|dns; startup-snapshot → summary|assemblies|modules|di|timeline. " +
        "Handles expire ~10 minutes after collection.")]
    public static DiagnosticResult<CollectionQueryResult> QueryCollection(
        IDiagnosticHandleStore handles,
        IPrincipalAccessor principalAccessor,
        [Description("Handle returned by a prior collection tool, especially collect_events with kind one of counters/exceptions/crash-guard/gc/datas/catalog/event_source/activities/logs/jit/threadpool/contention/db/kestrel/networking/startup. ")] string handle,
        [Description("View name (kind-dependent). Defaults to 'summary', except event-catalog defaults to 'catalog'.")] string? view = null,
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

        return QueryCollection(entry.Value, principalAccessor, handle, view, topN, correlateArtifact);
    }

    internal static DiagnosticResult<CollectionQueryResult> QueryCollection(
        HandleLookup entry,
        IPrincipalAccessor principalAccessor,
        string handle,
        string? view,
        int topN,
        object? correlateArtifact = null)
    {
        var principal = principalAccessor.Current;
        if (principal is not null)
        {
            var requiredScope = entry.Kind == CollectionHandleKinds.Counters ? "read-counters" : "eventpipe";
            if (!principal.HasScope(requiredScope))
            {
                var message = $"forbidden: tool 'query_collection' requires scope '{requiredScope}' for kind '{entry.Kind}'.";
                return DiagnosticResult.Fail<CollectionQueryResult>(
                    message,
                    new DiagnosticError("Forbidden", message, requiredScope));
            }
        }

        if (entry.Kind == CollectionHandleKinds.EventCatalog && entry.Artifact is EventCatalogSnapshot catalogSnapshot)
        {
            var effectiveView = string.IsNullOrWhiteSpace(view) ? EventCatalogQueryDispatcher.CatalogView : view.Trim();
            var catalogResult = EventCatalogQueryDispatcher.Render(catalogSnapshot, handle, effectiveView, topN);
            if (catalogResult.IsError)
            {
                return new DiagnosticResult<CollectionQueryResult>(catalogResult.Summary, catalogResult.Hints, catalogResult.Error);
            }

            var queryResult = new CollectionQueryResult(
                CollectionHandleKinds.EventCatalog,
                effectiveView,
                catalogSnapshot.ProcessId,
                catalogSnapshot.StartedAt,
                catalogSnapshot.Duration,
                catalogResult.Data!);
            return DiagnosticResult.Ok(
                queryResult,
                $"Rendered view '{queryResult.View}' for kind '{queryResult.Kind}' (collected {queryResult.Duration.TotalSeconds:F1}s starting {queryResult.StartedAt:HH:mm:ss}Z, pid {queryResult.ProcessId}).",
                new NextActionHint("query_snapshot",
                    $"Switch to another view: {string.Join(" | ", EventCatalogQueryDispatcher.SessionViews)}.",
                    new Dictionary<string, object?> { ["handle"] = handle }));
        }

        var outcome = CollectionQueryDispatcher.Dispatch(entry.Kind, view, entry.Artifact, topN, correlateArtifact);

        if (outcome.UnknownKind is not null)
        {
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"Handle '{handle}' is of kind '{outcome.UnknownKind}' which query_collection does not support.",
                new DiagnosticError(
                    "UnsupportedHandleKind",
                    $"query_collection dispatches over kinds: {string.Join(", ", new[] { CollectionHandleKinds.Counters, CollectionHandleKinds.ExceptionSnapshot, CollectionHandleKinds.CrashGuardSnapshot, CollectionHandleKinds.GcEvents, CollectionHandleKinds.EventCatalog, CollectionHandleKinds.EventSource, CollectionHandleKinds.Activities, CollectionHandleKinds.LogSnapshot, CollectionHandleKinds.JitSnapshot, CollectionHandleKinds.ThreadPoolSnapshot, CollectionHandleKinds.ContentionSnapshot, CollectionHandleKinds.DbSnapshot, CollectionHandleKinds.KestrelSnapshot, CollectionHandleKinds.NetworkingSnapshot, CollectionHandleKinds.StartupSnapshot })}.",
                    outcome.UnknownKind),
                new NextActionHint("query_snapshot", "Use the kind-specific drill-down tool for heap/thread/cpu handles.", null));
        }
        if (outcome.UnknownView is not null)
        {
            var allowed = outcome.AllowedViews ?? Array.Empty<string>();
            return DiagnosticResult.Fail<CollectionQueryResult>(
                $"View '{outcome.UnknownView}' is not defined for kind '{entry.Kind}'.",
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
        "Subscribes to runtime exception/crash events and returns early if the target process exits. " +
        "Use before triggering a suspected fatal path; drill in with query_snapshot(handle, view=summary|exceptions|stack).")]
    public static async Task<DiagnosticResult<CrashGuardSnapshot>> CollectCrashGuard(
        ICrashGuardCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Guard window in seconds. Must be >= 1. Defaults to 10. The collector returns earlier when the process exits.")] int durationSeconds = 10,
        [Description("Maximum number of exception events to retain. Must be >= 1. Defaults to 100.")] int maxRecent = 100,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps headline/final-exception data inline and retains the exception stream behind the handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectCrashGuard(
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
        "Captures DATAS (Dynamic Adaptation To Application Sizes) GC tuning events: heap-count " +
        "decisions, per-GC samples and gen2 backstop tuning. Requires Server GC (DATAS is default-on " +
        "in .NET 9+); Workstation GC returns a graceful NoDatasEvents result.")]
    public static async Task<DiagnosticResult<GcDatasSnapshot>> CollectGcDatas(
        IGcDatasCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 15. DATAS decisions accrue over time, so give it a sustained window.")] int durationSeconds = 15,
        [Description("Maximum number of events to retain per kind (samples/tuning/gen2). Must be >= 1. Defaults to 1000.")] int maxEvents = 1000,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectGcDatas(
            collector, resolver, handles,
            processId, durationSeconds, maxEvents,
            cancellationToken).ConfigureAwait(false);
    }



    [RequireScope("eventpipe")]
    [Description(
        "Collects startup/cold-start signals from runtime LoaderKeyword assembly/module load events plus the Microsoft-Extensions-DependencyInjection EventSource. " +
        "Only events emitted during the collection window are captured when attaching to an already-running process; pre-attach cold-start events are missed. " +
        "JIT-at-startup is covered separately by collect_events(kind=\"jit\"). Drill in with query_snapshot(handle, view=summary|assemblies|modules|di|timeline).")]
    public static async Task<DiagnosticResult<StartupSnapshot>> CollectStartup(
        IStartupCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps headline aggregates and short loader/DI slices inline; the handle retains the full startup timeline.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectStartup(
            collector, resolver, handles,
            processId, durationSeconds, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Captures a broad metadata-only EventPipe catalog: provider, event name, level and timestamp. " +
        "Payload values are intentionally omitted; use collect_events(kind=event_source) for targeted payload capture.")]
    public static async Task<DiagnosticResult<EventCatalogSnapshot>> CollectEventCatalog(
        IEventCatalogCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Optional provider names. When omitted, enables a broad curated default set; custom EventSources must be named explicitly because EventPipe has no wildcard.")] IReadOnlyList<string>? providers = null,
        [Description("Maximum number of metadata-only occurrence samples to retain. Must be >= 1. Defaults to 200.")] int maxEvents = 200,
        [Description("Verbosity (summary|detail|raw). Default 'summary' drops Sample[] inline while keeping the ranked Catalog; the handle retains the bounded sample.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectEventCatalog(
            collector, resolver, handles,
            processId, durationSeconds, providers, maxEvents, depth,
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
        "Curates the Kestrel HTTP server pipeline by subscribing to the Microsoft-AspNetCore-Server-Kestrel EventSource. " +
        "Pairs connection/request/TLS start+stop events to compute request and TLS-handshake latency percentiles, tracks the " +
        "connection- and request-queue-length counters over time to localize head-of-line blocking, and captures the live " +
        "KestrelServerOptions JSON emitted by the Configuration event at session enable. " +
        "The full artifact is retained behind the issued handle — drill in with query_snapshot(handle, view=summary|byOperation|queues|tls|config).")]
    public static async Task<DiagnosticResult<KestrelSnapshot>> CollectKestrel(
        IKestrelCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Refresh interval (in seconds) requested from Kestrel EventCounters. Defaults to 1.")] int intervalSeconds = 1,
        [Description("Verbosity (summary|detail|raw). Default 'summary' trims the by-operation list and drops the queue timeline + config JSON inline; 'detail' and 'raw' return the full window.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectKestrel(
            collector, resolver, handles,
            processId, durationSeconds, intervalSeconds, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Enumerates the ASP.NET Core requests that are in-flight (started but not stopped) over a fixed EventPipe window — the key move for 'the app is hung, what's it doing?'. " +
        "Subscribes to the Microsoft.AspNetCore.Hosting HttpRequestIn Activity start/stop pairs via the Microsoft-Diagnostics-DiagnosticSource bridge; requests that started but did not stop before the window closed are reported with path, verb, elapsed time and trace-id, sorted oldest-first and flagging long-runners over a threshold. " +
        "Pure EventPipe — no ptrace — so it is safe against a hung production process. Drill in with query_snapshot(handle, view=summary|requests|longRunning); for the live thread stack behind a stuck request use inspect_process(view=requests-now) which adds ptrace-backed stacks.")]
    public static async Task<DiagnosticResult<InFlightRequestSnapshot>> CollectInFlightRequests(
        IInFlightRequestCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Elapsed-time threshold (in milliseconds) above which an in-flight request is flagged as long-running. Defaults to 1000.")] double longRunningThresholdMs = 1000,
        [Description("Maximum number of in-flight requests to return inline (oldest-first). Must be >= 1. Defaults to 100; the full set stays behind the handle.")] int maxRequests = 100,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps only the oldest in-flight requests inline; 'detail' and 'raw' return the full captured list.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectInFlightRequests(
            collector, resolver, handles,
            processId, durationSeconds, longRunningThresholdMs, maxRequests, depth,
            cancellationToken).ConfigureAwait(false);
    }

    [RequireScope("eventpipe")]
    [Description(
        "Collects a curated outbound-networking view by subscribing to the stable .NET networking EventSources " +
        "(System.Net.Http, System.Net.NameResolution, System.Net.Security, System.Net.Sockets). " +
        "Pairs request/lookup/handshake start+stop events to compute latency percentiles, reads HttpClient connection-pool time-in-queue, " +
        "counts socket connects, groups outbound HTTP by host + path, and snapshots the per-provider EventCounters. " +
        "The full artifact is always retained behind the issued handle — drill in with query_snapshot(handle, view=summary|byOperation|queue|tls|dns).")]
    public static async Task<DiagnosticResult<NetworkingSnapshot>> CollectNetworking(
        INetworkingCollector collector,
        IProcessContextResolver resolver,
        IDiagnosticHandleStore handles,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Duration of the collection window in seconds. Must be >= 1. Defaults to 10.")] int durationSeconds = 10,
        [Description("Refresh interval (in seconds) requested from the networking EventCounters. Defaults to 1.")] int intervalSeconds = 1,
        [Description("Verbosity (summary|detail|raw). Default 'summary' keeps only the top by-operation slice inline; 'detail' and 'raw' return the full by-operation list captured in the window.")]
        SamplingDepth depth = SamplingDepth.Summary,
        CancellationToken cancellationToken = default)
    {
        return await EventCollectionUseCases.CollectNetworking(
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
        [Description("EventSource provider name, e.g. 'System.Net.Http' or 'Microsoft.AspNetCore.Hosting'. Must be on the curated allowlist (see `Diagnostics:EventSourceAllowlist`) unless the bearer principal holds the 'eventsource-any' scope (docs/authorization.md#modifier-scopes) — or, on legacy deployments, `unsafeProvider=true` AND the server has `Diagnostics:AllowSensitiveHeapValues=true` (issue #165 / M2).")] string providerName,
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
        "**Human approval is required (defense in depth — docs/authorization.md#per-call-confirmation).** " +
        "When the client advertises the MCP **elicitation** capability the server requests a native " +
        "approve/deny decision in-call; if the human declines, nothing is written. For clients without " +
        "elicitation, approval falls back to the `confirm=true` parameter: without it the tool returns a " +
        "`confirmation_required` envelope describing what would have been written and writes nothing to " +
        "disk; the operator-facing client should surface this preview to a human and only retry with " +
        "`confirm=true` after explicit approval. The `dump-write` + `ptrace` scopes are still required " +
        "on top of approval.")]
    public static Task<DiagnosticResult<DumpToolResult>> CollectProcessDump(
        IProcessDumper dumper,
        IProcessContextResolver resolver,
        IPrincipalAccessor principalAccessor,
        ILoggerFactory? loggerFactory = null,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Dump type: 'Mini', 'Triage', 'WithHeap' or 'Full'. Defaults to Mini.")] ProcessDumpType dumpType = ProcessDumpType.Mini,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). Dump files are written with POSIX mode 0600.")] string? outputDirectory = null,
        [Description("Defense-in-depth confirmation flag — fallback for clients WITHOUT the MCP elicitation capability. Must be true to write a dump file when elicitation is unavailable; without it the tool returns a `confirmation_required` envelope describing what would have been written. Elicitation-capable clients are ALWAYS prompted natively and this flag is ignored for them (a human decline cannot be bypassed with confirm=true). See docs/authorization.md#per-call-confirmation")] bool confirm = false,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        RequestContext<CallToolRequestParams>? requestContext = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolHeapDump.CollectProcessDump(
            dumper,
            resolver,
            principalAccessor,
            loggerFactory,
            processId,
            dumpType,
            outputDirectory,
            confirm,
            investigationHandleId,
            requestContext,
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
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).") ] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolHeapDump.InspectDump(
            inspector,
            handles,
            symbolServerAllowlist,
            principalAccessor,
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
    internal const string HeapSnapshotKind = DiagnosticToolHeapDump.HeapSnapshotKind;

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
        [Description("When true, detect MulticastDelegate instances during the heap walk and group their invocation list by (target type, method) — surfaces 'event handler never unsubscribed' leaks. Cheap (folded into the existing heap pass).") ] bool includeDelegateTargets = false,
        [Description("When true, hash every System.String during the heap walk and rank by aggregate retained bytes — surfaces missing string-interning. Cheap (folded into the existing heap pass) but allocates one hash per unique string.")] bool includeDuplicateStrings = false,
        [Description("Optional NT_SYMBOL_PATH-style search path reserved for symbol-resolving heap drilldowns. Precedence: symbolPath > MCP_SYMBOL_PATH > _NT_SYMBOL_PATH > target MainModule directory. **Remote symbol servers are OFF by default (issue #165 / M3)** — any `srv*http(s)://…` segment must point at a host on `Diagnostics:SymbolServerAllowlist`.")] string? symbolPath = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolHeapDump.InspectLiveHeap(
            inspector,
            handles,
            resolver,
            symbolServerAllowlist,
            principalAccessor,
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

    public static Task<DiagnosticResult<LiveHeapInspection>> InspectGcDump(
        IGcDumpHeapSnapshotCollector collector,
        IDiagnosticHandleStore handles,
        IProcessContextResolver resolver,
        int? processId = null,
        int topTypes = 20,
        TimeSpan? timeout = null,
        bool exportTrace = false,
        CancellationToken cancellationToken = default)
        => DiagnosticToolHeapDump.InspectGcDump(
            collector,
            handles,
            resolver,
            processId,
            topTypes,
            timeout,
            exportTrace,
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
        "`timers` (live System.Threading.Timer / Task / TaskCompletionSource objects grouped by timer callback and task type), " +
        "`alc` (live AssemblyLoadContext instances, collectible contexts, loaded assemblies, and bounded retention hints for suspected collectible leaks), " +
        "`object` (dump one managed object by address — SOS !do equivalent), " +
        "`gcroot` (find a shortest GC-root chain for one object address — SOS !gcroot equivalent), " +
        "`objsize` (compute the transitive retained size rooted at one object address — SOS !objsize equivalent), " +
        "`async` (pending async state machines reconstructed from the heap — state, awaiter type, and best-effort continuation chain à la SOS DumpAsync). " +

        "Handles expire ~10 minutes after the capture and are invalidated when the target process exits (live origin only).")]
    public static Task<DiagnosticResult<HeapSnapshotQueryResult>> QueryHeapSnapshot(
        IDiagnosticHandleStore handles,
        IDumpInspector inspector,
        SensitiveDataRedactor redactor,
        SensitiveValueGate sensitiveGate,
        IPrincipalAccessor principalAccessor,
        [Description("Snapshot handle returned by inspect_dump or inspect_live_heap.")] string handle,
        [Description("Which slice of the snapshot to return: 'top-types', 'retention-paths', 'roots-by-kind', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'gchandles', 'timers', 'alc', 'object', 'gcroot', 'objsize' or 'async'.")] string view = "top-types",
        [Description("Maximum entries to return for any ranked view ('top-types', 'finalizer-queue', 'fragmentation', 'static-fields', 'delegate-targets', 'duplicate-strings', 'async', 'timers', 'alc'). Ignored by 'roots-by-kind', 'gchandles', 'retention-paths', 'object', 'gcroot' and 'objsize'.")] int topN = 50,
        [Description("For view='top-types': ranking — 'bytes' (default) or 'instances'.")] string rankBy = "bytes",
        [Description("For view='retention-paths': case-insensitive substring matched against TypeFullName to narrow the returned chains.")] string? typeFullName = null,
        [Description("For view='object', 'gcroot' and 'objsize': managed object address (decimal or 0x-prefixed hex).") ] string? address = null,
        [Description("Opt-in to return raw string content / field value previews on the 'duplicate-strings' and 'object' views (issue #165 / H4). Defaults to false — those fields are returned as metadata-only placeholders unless the server enables `Diagnostics:AllowSensitiveHeapValues=true` AND the caller passes `includeSensitiveValues=true`. Any string surfaced even in that mode still runs through the SensitiveDataRedactor (Bearer/PEM/JWT/connection-string/AWS-key patterns).") ] bool includeSensitiveValues = false,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolHeapDump.QueryHeapSnapshot(
            handles,
            inspector,
            redactor,
            sensitiveGate,
            principalAccessor,
            handle,
            view,
            topN,
            rankBy,
            typeFullName,
            address,
            includeSensitiveValues,
            deprecation,
            cancellationToken);

    internal const string ThreadSnapshotKind = DiagnosticToolThreadingAndJit.ThreadSnapshotKind;

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
        "query_snapshot. Dump-origin handles are NOT evicted when the producer PID exits.")]
    public static Task<DiagnosticResult<ThreadSnapshotQueryResult>> CollectThreadSnapshot(
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
        [Description("Verbosity (summary|detail|raw). Default 'summary' returns only the top-3 blocked threads inline and drops the SyncBlock lock-graph (use query_snapshot(handle, view=\"lock-graph\") for the full graph). 'detail' returns the historical top-25 threads + top-25 locks. 'raw' is equivalent to detail. The full snapshot is always retained behind the issued handle.")]
        SamplingDepth depth = SamplingDepth.Summary,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        LegacyDiagnosticsFlagDeprecation? deprecation = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolThreadingAndJit.CollectThreadSnapshot(
            inspector,
            handles,
            resolver,
            symbolServerAllowlist,
            principalAccessor,
            processId,
            dumpFilePath,
            maxFramesPerThread,
            includeRuntimeFrames,
            includeNativeFrames,
            symbolPath,
            depth,
            investigationHandleId,
            deprecation,
            cancellationToken);

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
        "collect_events(kind=\"event_source\", providerName=\"Microsoft-Windows-DotNETRuntime\", keywords=Jit|JitTracing). " +
        "Supply at most ONE of processId or dumpFilePath: processId attaches via ClrMD with " +
        "suspend (sub-second for a single method); dumpFilePath analyses a WithHeap/Full dump " +
        "offline. When both are omitted the server auto-selects a live .NET process. The " +
        "method is identified by its (moduleVersionId, metadataToken) handoff key — the same " +
        "key dotnet-assembly-mcp uses. Optional codeAddress (e.g. from a MethodLoad_V2 event) " +
        "is a fast-path; tier is an informational label echoed back (ClrMD does not expose " +
        "the JIT OptimizationTier directly). NativeAOT and pure ReadyToRun targets are not " +
        "supported — disassemble the on-disk binary with dotnet-native-mcp.disassemble instead.")]
    public static Task<DiagnosticResult<CapturedMethodBytes>> CaptureMethodBytes(
        IJitMethodCapturer capturer,
        IProcessContextResolver resolver,
        [Description("PE module MVID (D format, e.g. '6f5c9bf0-1e0b-4f3b-9a8e-…') of the assembly that declares the method. Required.")] string moduleVersionId,
        [Description("IL method-def metadata token (table 0x06). Accepts decimal or hex (0x06000142). Required.")] string metadataToken,
        [Description("Operating system process id of the target .NET process. Mutually exclusive with dumpFilePath. Optional — when both processId and dumpFilePath are null/empty the server auto-selects a live .NET process.")] int? processId = null,
        [Description("Absolute path to a previously-captured .dmp file. Mutually exclusive with processId.")] string? dumpFilePath = null,
        [Description("Optional fast-path: a code address already observed for this method (e.g. MethodCodeStart from MethodLoad_V2). Hex (with or without 0x prefix) or decimal. Mismatches with (moduleVersionId, metadataToken) surface as a warning, not a hard error.")] string? codeAddress = null,
        [Description("Optional tier label echoed back on the result (e.g. 'Tier0', 'Tier1', 'Tier1OSR'). ClrMD does not expose the JIT OptimizationTier directly; this field is informational. The authoritative MethodCompilationType (None/Jit/Ngen) is always returned.")] string? tier = null,
        [Description("Optional sub-path under the artifact root (MCP_ARTIFACT_ROOT, default <temp>/dotnet-diagnostics-mcp). Defaults to 'method-bytes/{pid}'. MUST be relative — absolute paths and '..' traversal are rejected (InvalidArtifactPath). .bin files are written with POSIX mode 0600.")] string? outputDirectory = null,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolThreadingAndJit.CaptureMethodBytes(
            capturer,
            resolver,
            moduleVersionId,
            metadataToken,
            processId,
            dumpFilePath,
            codeAddress,
            tier,
            outputDirectory,
            investigationHandleId,
            cancellationToken);

    [RequireScope("ptrace")]
    [Description(
        "Returns a slice of a thread snapshot previously captured by collect_thread_snapshot, addressed by its handle. Views: " +
        "`threads-summary` (every managed thread with state + top frame), " +
        "`stack` (full captured frames of one thread — requires `threadId`; for `linux-native-stack` snapshots this is the OS thread id / TID), " +
        "`lock-graph` (every SyncBlock that is held or contended, sorted by waiter count then recursion), " +
        "`deadlocks` (wait-for cycle detection over the captured lock graph, with lock chains and suggested SOS follow-up commands), " +
        "`top-blocked` (top-N likely blocked threads), " +
        "`unique-stacks` (group by identical top-of-stack prefixes to spot a stuck herd), " +
        "`async-stalls` (best-effort grouping of async state-machine waits; useful when no SyncBlocks are contended), " +
        "`wait-chains` (Linux-native-stack only: groups off-CPU kernel wait stacks into representative chains; empty on exact CoreCLR snapshots), and " +
        "`threadpool` (SOS !threadpool-style snapshot of worker/IOCP counts plus global/local queue depths and pending work items when the backend captured them). " +
        "Handles expire ~10 minutes after capture; live-origin handles are invalidated when the target PID exits.")]
    public static DiagnosticResult<ThreadSnapshotQueryResult> QueryThreadSnapshot(
        IDiagnosticHandleStore handles,
        [Description("Snapshot handle returned by collect_thread_snapshot.")] string handle,
        [Description("Which slice to return: 'threads-summary', 'stack', 'lock-graph', 'deadlocks', 'top-blocked', 'unique-stacks', 'async-stalls', 'wait-chains' or 'threadpool'.")] string view = "top-blocked",
        [Description("For view='stack': thread id key to return frames for. CoreCLR snapshots use ManagedThreadId; linux-native-stack snapshots use OSThreadId (TID). Ignored by other views.")] int? threadId = null,
        [Description("Maximum entries returned by ranked-list views ('threads-summary', 'top-blocked', 'lock-graph', 'unique-stacks') or the number of deadlock cycles returned by 'deadlocks'. Defaults to 50.")] int topN = 50,
        [Description("For view='unique-stacks': number of top frames folded into the signature hash. Defaults to 20. Ignored by other views.")] int framesToHash = ThreadSnapshotUniqueStackGrouper.DefaultFramesToHash,
        [Description("For view='unique-stacks': drop groups with fewer than this many threads. Defaults to 1. Ignored by other views.")] int minCount = 1)
        => DiagnosticToolThreadingAndJit.QueryThreadSnapshot(handles, handle, view, threadId, topN, framesToHash, minCount);

    [Description(
        "Streams a PE or PDB for a loaded managed module in repeated CallTool chunks so sibling MCPs can materialise pod-local binaries through the orchestrator proxy. " +
        "Resolve the module by ModuleVersionId (GUID 'D'); asset defaults to 'pe'. For PDBs the tool prefers a sibling .pdb next to the module, then falls back to an embedded portable PDB inside the PE. " +
        "processId is optional — when omitted the server auto-selects a live .NET process via the usual resolver. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static Task<DiagnosticResult<ByteFetchEnvelope>> GetModuleBytes(
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
        => DiagnosticToolByteStreaming.GetModuleBytes(
            moduleByteSource,
            resolver,
            principalAccessor,
            moduleVersionId,
            asset,
            offset,
            maxBytes,
            processId,
            loggerFactory,
            cancellationToken);

    [Description(
        "Streams a dump file under the artifact root in repeated CallTool chunks so sibling MCPs can materialise pod-local dumps through the orchestrator proxy. dumpFilePath may be relative to MCP_ARTIFACT_ROOT or absolute when it still resolves under that root after symlink resolution. " +
        "Path hints are untrusted: the tool re-validates every call through the artifact-root sandbox. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static Task<DiagnosticResult<ByteFetchEnvelope>> GetDumpBytes(
        IDumpByteSource dumpByteSource,
        IPrincipalAccessor principalAccessor,
        [Description("Dump path to stream. Relative paths are resolved under the artifact root; absolute paths are allowed only when they still resolve under that root. Required.")] string dumpFilePath,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolByteStreaming.GetDumpBytes(
            dumpByteSource,
            principalAccessor,
            dumpFilePath,
            offset,
            maxBytes,
            loggerFactory,
            cancellationToken);

    [Description(
        "Streams a raw trace file (.nettrace) under the artifact root in repeated CallTool chunks so a sibling MCP / human can materialise an exported CPU or GC trace for offline PerfView/Speedscope/Perfetto analysis. traceFilePath may be relative to MCP_ARTIFACT_ROOT or absolute when it still resolves under that root after symlink resolution. " +
        "Path hints are untrusted: the tool re-validates every call through the artifact-root sandbox. maxBytes defaults to 4 MiB and is capped at 16 MiB per response; total artifact size is capped at 256 MiB.")]
    public static Task<DiagnosticResult<ByteFetchEnvelope>> GetTraceBytes(
        IDumpByteSource traceByteSource,
        IPrincipalAccessor principalAccessor,
        [Description("Trace path to stream. Relative paths are resolved under the artifact root; absolute paths are allowed only when they still resolve under that root. Required.")] string traceFilePath,
        [Description("Byte offset where this chunk starts. Defaults to 0.")] long offset = 0,
        [Description("Maximum bytes to return in this response. Defaults to 4 MiB and is capped at 16 MiB.")] int maxBytes = FileChunkReader.DefaultChunkBytes,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolByteStreaming.GetTraceBytes(
            traceByteSource,
            principalAccessor,
            traceFilePath,
            offset,
            maxBytes,
            loggerFactory,
            cancellationToken);

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
        "(USE-style: collect_events(kind=\"counters\") first, branch on evidence). Call this BEFORE any other " +
        "collector when the symptom is non-trivial — it pays for itself by preventing loops.")]
    public static Task<DiagnosticResult<InvestigationPlan>> StartInvestigation(
        IInvestigationPlanner planner,
        IProcessContextResolver resolver,
        [Description("Operating system process id of the target .NET process. Optional — server auto-selects when only one .NET process is visible.")] int? processId = null,
        [Description("Plain-language symptom, e.g. 'high latency on /checkout since v2025.10'. Required for cold mode; optional for warm/hypothesis.")] string? symptom = null,
        [Description("Specific hypothesis to test, e.g. 'lock contention on Cart.Checkout'. Triggers hypothesis mode.")] string? hypothesis = null,
        [Description("Baseline snapshot from a prior investigation (JSON of BaselineHandle). Triggers warm mode.")] BaselineHandle? baseline = null,
        [Description("Optional hard limit on tool calls before forcing summarization. Defaults to 8.")] int maxToolCalls = 8,
        [Description("If true, collect_process_dump steps are marked approval-gated. Defaults to true.")] bool dumpRequiresApproval = true,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null,
        CancellationToken cancellationToken = default)
        => DiagnosticToolInvestigationPlanning.StartInvestigation(
            planner,
            resolver,
            processId,
            symptom,
            hypothesis,
            baseline,
            maxToolCalls,
            dumpRequiresApproval,
            investigationHandleId,
            cancellationToken);

    [RequireScope("investigation-export")]
    [McpServerTool(
        Name = "export_investigation_summary",
        Title = "Export portable investigation summary",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Reads a prior collect_sample(kind=\"cpu\") drill-down handle and produces a portable, versioned " +
        "InvestigationSummary (~5-20 KB JSON) ready to paste into a PR, ADR, or ticket. " +
        "Includes build + container provenance harvested from the sidecar environment, stable " +
        "module+methodFullName symbol refs (survive rebuilds where line numbers shift), and " +
        "optional lineage to a previous investigation. Set `format=markdown` for a human-readable " +
        "version. The server is stateless: the LLM owns persistence — paste the JSON into a doc " +
        "and feed it back via `compare_to_baseline` on the next deploy. When the operator opts in " +
        "(MCP_INVESTIGATION_OTEL=1) the summary is also emitted as an OpenTelemetry span for " +
        "durable, queryable investigation history; off by default.")]
    public static DiagnosticResult<ExportedInvestigationSummary> ExportInvestigationSummary(
        IInvestigationSummaryExporter exporter,
        IDiagnosticHandleStore handles,
        DotnetDiagnostics.Mcp.Observability.IInvestigationTelemetryEmitter telemetry,
        [Description("Handle returned by a prior collect_sample(kind=\"cpu\") call.")] string handle,
        [Description("Output format: 'json' (default — portable, machine-readable) or 'markdown' (human-readable for PRs).")] SummaryFormat format = SummaryFormat.Json,
        [Description("Max hotspots to include in the summary. Defaults to 10.")] int topHotspots = 10,
        [Description("Optional managed assembly name for the target (from list_dotnet_processes).")] string? buildAssemblyName = null,
        [Description("Optional investigation id from the previous summary, to link lineage.")] string? previousInvestigationId = null,
        [Description("Optional commit SHA being proposed as the fix.")] string? fixCommitSha = null,
        [Description("Optional PR URL being proposed as the fix.")] string? fixPullRequestUrl = null,
        [Description("Optional short description of the proposed fix.")] string? fixDescription = null,
        [Description("Optional free-form notes appended to the summary.")] string? notes = null,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null)
        => DiagnosticToolInvestigationPlanning.ExportInvestigationSummary(
            exporter,
            handles,
            telemetry,
            handle,
            format,
            topHotspots,
            buildAssemblyName,
            previousInvestigationId,
            fixCommitSha,
            fixPullRequestUrl,
            fixDescription,
            notes,
            investigationHandleId);

    [RequireScope("investigation-export")]
    [McpServerTool(
        Name = "compare_to_baseline",
        Title = "Compare investigation summary to baseline",
        Destructive = false,
        ReadOnly = true,
        Idempotent = true,
        UseStructuredContent = true)]
    [Description(
        "Diffs either two InvestigationSummary JSON documents (produced by export_investigation_summary) " +
        "or 2..N persisted ComparableSnapshot JSON documents. Legacy summaries return the same " +
        "SummaryDiff as before; comparable snapshots return either the full SnapshotJourneyDiff when small " +
        "or a compact verdict/headline/top-deltas summary with a journey://diff/{handle} Resource link for large matrices. " +
        "Pass JSON bodies only; the stateless sidecar never reads comparison inputs from file paths.")]
    public static DiagnosticResult<object> CompareToBaseline(
        ISummaryComparer comparer,
        IDiagnosticHandleStore handles,
        [Description("Baseline summary JSON (from a prior export_investigation_summary). Optional when snapshotsJson is supplied.")] string? baselineSummaryJson = null,
        [Description("Current summary JSON (from export_investigation_summary on the new investigation). Optional when snapshotsJson is supplied.")] string? currentSummaryJson = null,
        [Description("Ordered ComparableSnapshot JSON bodies to compare as a journey. JSON bodies only; do not pass file paths.")] string[]? snapshotsJson = null,
        [Description("ComparableSnapshot journey only: maximum metric series / key rows returned in compact inline payloads and used to bound key-matrix construction. Must be >= 1. Defaults to 25.")] int topN = 25,
        [Description("ComparableSnapshot journey only: inline verbosity. `full` returns the full matrix when it is below the inline threshold; `compact` returns verdict/headline/counts/notes plus top-N metric and key deltas. Large full diffs always return compact inline data plus a journey://diff/{handle} Resource link. Defaults to `full`.")] string depth = "full",
        [Description("ComparableSnapshot journey only: `trend` (default) compares ordered captures over time; `dispersion` compares unordered replicas for outliers.")] string? mode = null,
        [Description("Optional orchestrator investigation handle returned by attach_to_pod. When supplied, the orchestrator routes this diagnostic call through that attached Pod instead of inferring routing from the current MCP session binding.")]
        string? investigationHandleId = null)
        => DiagnosticToolBaselineComparison.CompareToBaseline(
            comparer,
            handles,
            baselineSummaryJson,
            currentSummaryJson,
            snapshotsJson,
            topN,
            depth,
            mode,
            investigationHandleId);

    private static DiagnosticResult<T> InvalidArg<T>(string parameterName, string requirement)
        => DiagnosticResult.Fail<T>(
            $"Argument '{parameterName}' {requirement}.",
            new DiagnosticError("InvalidArgument", $"Argument '{parameterName}' {requirement}.", parameterName),
            new NextActionHint("inspect_process", "Re-issue with valid arguments. See tool schema for ranges and defaults."));

    /// <summary>
    /// B4 / issue #165 / M3 helper: returns a denial envelope when the caller-supplied
    /// <paramref name="symbolPath"/> references a remote symbol-server host that is not on
    /// the configured allowlist. Returns <c>null</c> when the path is allowed (local path,
    /// empty/null, or remote host on the allowlist) so the caller can early-return only on
    /// denial. Must be invoked from every tool that forwards a caller-supplied
    /// <c>symbolPath</c> into a SymbolReader / native symbolicator backend. B5.2 layers a
    /// principal-side modifier scope on top: callers holding <c>symbols-remote</c>
    /// (docs/authorization.md#scopes) bypass the allowlist entirely. The legacy server-wide allowlist
    /// keeps working byte-for-byte for principals without the scope.
    ///
    /// B5.4 / docs/authorization.md#backward-compatibility: when the allowlist (not the scope) was the path that allowed
    /// a remote host through, fires a once-per-process deprecation warning via
    /// <paramref name="deprecation"/>. The allowlist policy itself is retained — only the
    /// pattern of relying on a single deployment-wide allowlist for caller-level
    /// distinction is deprecated.
    /// </summary>
    // Symbol-path validation lives in Core (UseCases/SymbolPathValidation) since #288 so the CLI and
    // the MCP tools share one source of truth. This thin forward hoists the transport-specific bypass
    // decision (the docs/authorization.md `symbols-remote` scope) into a precomputed bool and adapts the Server's
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
