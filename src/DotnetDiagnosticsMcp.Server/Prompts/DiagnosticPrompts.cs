using System.ComponentModel;
using ModelContextProtocol.Server;

namespace DotnetDiagnosticsMcp.Server.Prompts;

/// <summary>
/// Server-side Prompts that pre-package an investigation strategy for the most common .NET
/// performance complaints. The LLM still drives the tool calls, but the prompt gives it the
/// curated playbook without burning context on a long system prompt — and clients can surface
/// each prompt as a one-click action.
/// </summary>
[McpServerPromptType]
public sealed class DiagnosticPrompts
{
    [McpServerPrompt(Name = "diagnose-slow-app", Title = "Diagnose slow .NET app")]
    [Description(
        "Step-by-step CPU/latency investigation for a running .NET process. " +
        "Starts from a counter baseline, branches into CPU sampling and exception storms based on what the data shows.")]
    public static string DiagnoseSlowApp(
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Optional human-readable hint about the symptom (e.g. 'p99 latency doubled after 14:00').")] string? symptom = null)
        => $$"""
        Goal: explain why process {{processId}} is slow{{(string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.")}}

        Execute the following plan, calling each tool through this MCP server. Read every response's `summary` and follow the first `hint` unless a previous step already disproved it.

        1. Call `get_process_info` with processId={{processId}} to confirm the target is still alive and capture entrypoint + runtime version.
        2. Call `get_diagnostic_capabilities` with processId={{processId}}. If `CanSampleCpu` is false (NativeAOT), substitute step 4 with `collect_event_source(providerName="System.Threading", durationSeconds=10)` and note the limitation.
        3. Call `snapshot_counters` with processId={{processId}}, durationSeconds=10. Read `data.counters` for `cpu-usage`, `threadpool-queue-length`, `monitor-lock-contention-count`, `gc-heap-size`, `exception-count`.
        4. If `cpu-usage` >= 70%: call `collect_cpu_sample` with processId={{processId}}, durationSeconds=10, topN=20. Report the top 5 inclusive hotspots.
        5. If `monitor-lock-contention-count` is climbing OR `threadpool-queue-length` > 0 sustained: call `collect_event_source(providerName="System.Threading.Tasks.TplEventSource", durationSeconds=10, maxEvents=500)`.
        6. If `exception-count` is climbing: call `collect_exceptions(processId={{processId}}, durationSeconds=10, maxRecent=20)` and surface the top 3 types.
        7. Synthesize: name the suspected hot path (method/area), the evidence, and propose either a code-level fix or a follow-up collection (e.g. `collect_process_dump` if root cause is unclear and the symptom can reproduce).

        Hard rules:
        - Never call `collect_process_dump` unless steps 3–6 have not narrowed the cause OR the user explicitly asks for one.
        - Always pass `durationSeconds` <= 30. Re-run with a longer window only if the first attempt returned empty data.
        - If any step returns a structured error (envelope `error.Kind`), follow its `hints` before continuing.
        """;

    [McpServerPrompt(Name = "diagnose-memory-growth", Title = "Diagnose memory growth")]
    [Description(
        "GC and allocation investigation for a process whose working set or managed heap keeps climbing. " +
        "Bounds dump cost by starting from counters + GC events and only resorting to a heap dump when justified.")]
    public static string DiagnoseMemoryGrowth(
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Optional human-readable hint (e.g. 'heap grows 20MB/min after first request').")] string? symptom = null)
        => $$"""
        Goal: explain memory growth in process {{processId}}{{(string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.")}}

        1. `get_process_info(processId={{processId}})` — confirm liveness.
        2. `get_diagnostic_capabilities(processId={{processId}})` — note `CanCollectGcDump`. NativeAOT cannot collect gcdump; you must rely on counters + heap dump.
        3. `snapshot_counters(processId={{processId}}, durationSeconds=10)` — read `gc-heap-size`, `gen-0-size`, `gen-1-size`, `gen-2-size`, `loh-size`, `poh-size`, `gc-fragmentation`. Decide:
           - Gen2 grows monotonically → leak or large cache; continue.
           - LOH grows → large object allocations; continue.
           - Working set grows but managed heap stable → native / unmanaged growth (mention dotnet-monitor or native profiler as out-of-scope).
        4. `collect_gc_events(processId={{processId}}, durationSeconds=15, maxEvents=200)` — inspect `data.maxPauseTime` and `data.events`. Frequent gen-2 collections with no shrink = retention.
        5. If retention is suspected, call `collect_process_dump(processId={{processId}}, dumpType="WithHeap")`. Report the resulting file path so the user can open it in `dotnet-dump analyze`.
        6. Synthesize: leaking type (if known), suggested next analysis (`!dumpheap -stat`, `!gcroot`), or a recommended code area to audit.

        Hard rules:
        - Only one `WithHeap` dump per investigation unless asked otherwise — they're expensive.
        - If `CanCollectGcDump` is false, skip step 4 in favor of `collect_event_source(providerName="Microsoft-Windows-DotNETRuntime")` filtered to GC keywords.
        """;

    [McpServerPrompt(Name = "diagnose-exception-storm", Title = "Diagnose exception storm")]
    [Description(
        "Investigation for a process where `exception-count` is spiking or first-chance exceptions are suspected to be driving latency/CPU.")]
    public static string DiagnoseExceptionStorm(
        [Description("Operating system process id of the target .NET process.")] int processId,
        [Description("Optional human-readable hint (e.g. 'errors spike when endpoint /api/orders is called').")] string? symptom = null)
        => $$"""
        Goal: identify what's throwing in process {{processId}}{{(string.IsNullOrWhiteSpace(symptom) ? "." : $" — context: {symptom}.")}}

        1. `get_process_info(processId={{processId}})` — sanity check.
        2. `snapshot_counters(processId={{processId}}, durationSeconds=10)` — confirm `exception-count` is actually climbing and capture the rate.
        3. `collect_exceptions(processId={{processId}}, durationSeconds=15, maxRecent=30)` — read `data.byType` for the dominant exception type(s) and `data.recent` for stack frames.
        4. If exceptions correlate with HTTP traffic, also call `collect_event_source(providerName="System.Net.Http", durationSeconds=10, maxEvents=200)` and join the activities with the exception timestamps.
        5. If a single type dominates AND its message points at a control-flow use (e.g. `KeyNotFoundException`, `FormatException` from `int.Parse`), recommend replacing with `TryGet*`/`TryParse`.
        6. If exceptions are wrapped/rethrown across boundaries, suggest enabling first-chance logging in the offending module rather than another collection run.

        Hard rules:
        - Start `collect_exceptions` BEFORE the workload that triggers the storm — exceptions thrown before the session starts are invisible.
        - Don't escalate to `collect_process_dump` unless step 3 returns zero exceptions despite a non-zero `exception-count` (indicates lost events / sampler limit).
        """;
}
