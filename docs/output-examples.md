# Output examples

> **What each capture actually returns.** Real, trimmed output for the workhorse capture
> families, so you know what to expect before wiring a client. All live samples below were
> **captured in v0.13.0** against the in-repo samples on Linux (.NET 10.0.5).

The same Core capture engine backs all three deliverables, so the **shape** is identical no
matter how you invoke it — only the entry point differs:

| Track | How you invoke a capture |
|---|---|
| **MCP server** (`dotnet-diagnostics-mcp`) | `collect_events(kind=…)`, `collect_sample(kind=…)`, `inspect_process(view=…)` — see [`tool-reference.md`](./tool-reference.md) |
| **CLI** (`dotnet-diagnostics-cli`) | `dotnet-diagnostics-cli collect --kind … --pid <pid> --json` — see [`cli-reference.md`](./cli-reference.md) |
| **BenchmarkDotNet diagnoser** | `[DiagnosticKind("cpu")]` on a `[Benchmark]` — see [`../src/DotnetDiagnostics.BenchmarkDotNet/README.md`](../src/DotnetDiagnostics.BenchmarkDotNet/README.md) |

Every result is the same envelope: a one-line **`summary`**, zero or more **`hints`** (the
suggested next tool), and a **`data`** payload. The JSON below is **trimmed** — long arrays are
cut to one or two representative entries (`// … N more`) and timestamps/ids elided for brevity.

> **How these were captured.** Event-collector families (`counters`, `gc`, `exceptions`,
> `threadpool`, `contention`) were captured live with `dotnet-diagnostics-cli collect` against
> [`samples/BadCodeSample`](../samples/BadCodeSample); the samplers (`cpu`, `allocation`) were
> captured by the BenchmarkDotNet diagnoser running [`benchmarks/DiagnosedBenchmarks`](../benchmarks/DiagnosedBenchmarks).

---

## Triage signal — `counters`

EventCounters snapshot. The cheapest first look; the `hints` already classify the workload.

```
dotnet-diagnostics-cli collect --kind counters --pid <pid> --duration 6 --json
```

```jsonc
{
  "summary": "Captured 33 counter(s) and 0 meter series over 6s — cpu-usage=0.0%, gc-heap-size=4.8.",
  "hints": [
    { "nextTool": "collect",      "reason": "time-in-gc=44.0% — GC pressure detected." },
    { "nextTool": "inspect-heap", "reason": "GC pressure — inspect heap for allocation patterns." }
  ],
  "data": {
    "processId": 845384,
    "duration": "00:00:06",
    "counters": [
      { "provider": "System.Runtime", "name": "cpu-usage",      "displayName": "CPU Usage",                 "value": 0.0243,   "kind": 0 },
      { "provider": "System.Runtime", "name": "time-in-gc",     "displayName": "% Time in GC since last GC","value": 44,       "kind": 0 },
      { "provider": "System.Runtime", "name": "gc-heap-size",   "displayName": "GC Heap Size",              "value": 4.80824,  "kind": 0 },
      { "provider": "System.Runtime", "name": "alloc-rate",     "displayName": "Allocation Rate",           "value": 8200,     "kind": 1 },
      { "provider": "System.Runtime", "name": "working-set",    "displayName": "Working Set",               "value": 104.4316, "kind": 0 }
      // … 28 more
    ],
    "meters": [],
    "notes": []
  }
}
```

---

## Event collectors

EventPipe streams aggregated into a structured rollup. Start the collection **before** the load
that generates the events (sessions take ~0.5–1 s to start).

### `gc`

```
dotnet-diagnostics-cli collect --kind gc --pid <pid> --duration 10 --json
```

```jsonc
{
  "summary": "4 collection(s), max pause 3.8ms, total pause 7.1ms.",
  "hints": [
    { "nextTool": "collect", "reason": "GC looks healthy — pivot to a domain EventSource for application-level signal." }
  ],
  "data": {
    "totalCollections": 4,
    "totalPauseTime": "00:00:00.0071",
    "maxPauseTime": "00:00:00.0038",
    "generations": [ { "generation": 2, "count": 4 } ],
    "events": [
      { "generation": 2, "reason": "AllocLarge", "type": "NonConcurrentGC", "pauseDuration": "00:00:00.0038057" }
      // … 3 more
    ]
  }
}
```

### `exceptions`

```
dotnet-diagnostics-cli collect --kind exceptions --pid <pid> --duration 10 --json
```

```jsonc
{
  "summary": "300 exception(s) over 10s; most common: System.FormatException (300).",
  "hints": [ { "nextTool": "collect", "reason": "Subscribe to a domain-specific EventSource to correlate." } ],
  "data": {
    "totalExceptions": 300,
    "byType": [ { "exceptionType": "System.FormatException", "count": 300 } ],
    "recent": [
      {
        "exceptionType": "System.FormatException",
        "exceptionMessage": "The input string 'not-a-number' was not in a correct format."
      }
      // … capped at 100 recent
    ],
    "recentCap": 100
  }
}
```

### `threadpool`

```
dotnet-diagnostics-cli collect --kind threadpool --pid <pid> --duration 10 --json
```

```jsonc
{
  "summary": "Captured ThreadPool activity over 10s: workers latest/peak=9/9, hill-climbing events=9, starvation reasons=9, enqueue/dequeue=0/0.",
  "hints": [],
  "data": {
    "workerThreadTimeline": [ { "count": 0 } /* … 9 more */ ],
    "iocpThreadTimeline": [],
    "hillClimbing": [ { "reason": "Starvation", "oldCount": 0, "newCount": 1 } /* … 8 more */ ],
    "workItemOrigins": [],
    "notes": [ "Effective MinThreads/MaxThreads unavailable from the EventPipe-only ThreadPool collector." ]
  }
}
```

### `contention`

```
dotnet-diagnostics-cli collect --kind contention --pid <pid> --duration 10 --json
```

```jsonc
{
  "summary": "Captured 67 lock-contention event(s) over 10s across 2 contended monitor(s). Total wait=59610.6ms, p95=2228.5ms, max=2228.6ms.",
  "hints": [],
  "data": {
    "totalEvents": 67,
    "distinctMonitors": 2,
    "totalContentionDuration": "00:00:59.6106",
    "p95ContentionDuration": "00:00:02.2285",
    "maxContentionDuration": "00:00:02.2285",
    "events": [
      { "duration": "00:00:02.2285760", "contendingThreadId": 847990, "lockId": 104154062841664 }
      // … 66 more
    ],
    "notes": [ "ContentionStart call stacks require a TraceLog-backed session; byCallSite falls back to '(unknown)'." ]
  }
}
```

---

## Samplers (in-process via BenchmarkDotNet)

The BenchmarkDotNet diagnoser runs the sampler **out-of-process** against the benchmark child,
so the measurement itself does not perturb the captured allocation/CPU profile. Each
`[DiagnosticKind]` benchmark prints a one-line indicator under
`// * dotnet-diagnostics indicators *` and writes the full envelope as a `.json` artifact.

### `cpu` — per-frame self vs inclusive cost

`[DiagnosticKind("cpu")]` indicator line:

```
[cpu] Captured 2035 sample(s) over 5s across 25 hotspot(s).
      Hottest self-cost: System.Threading.Thread.<PollGC>g__PollGCWorker|67_0()
      (77.8% exclusive — 1584 self / 1584 inclusive sample(s)).
```

Each hotspot carries a **MethodIdentity** ready to hand off to `dotnet-assembly-mcp.get_method`:

```jsonc
{
  "Data": {
    "TotalSamples": 2035,
    "TopHotspots": [
      {
        "Frame": {
          "Module": "System.Private.CoreLib",
          "Method": "System.Reflection.MethodBaseInvoker.InvokeDirectByRefWithFewArgs(...)"
        },
        "InclusiveSamples": 2030,
        "ExclusiveSamples": 1,
        "Identity": {
          "MethodName": "InvokeDirectByRefWithFewArgs",
          "ModuleVersionId": "e8c78f6b-682f-46b5-9b01-b6df65585a7b",
          "MetadataToken": 100696725,
          "TypeFullName": "System.Reflection.MethodBaseInvoker"
        }
      }
      // … 24 more hotspots
    ]
  }
}
```

> `ExclusiveSamples` (self) is the cost **in** that method; `InclusiveSamples` is the cost of it
> **plus everything it called**. A high inclusive / low exclusive frame is a router; a high
> exclusive frame is the actual hotspot.

### `allocation` — type **and** call-site origin

`[DiagnosticKind("allocation")]` indicator line — note the **`Top site:`** suffix (the
allocation *origin*, new in v0.13.0):

```
[allocation] Captured 342816 allocation event(s) (36,545,226,016 bytes) over 5s across 3 type(s).
             Top by bytes: System.String (36,545,011,024 bytes, 342814 event(s), Small heap).
             Top site: System.Private.CoreLib!System.String.Ctor(wchar,int32)
             (36,544,797,824 bytes, 342812 event(s)).
```

```jsonc
{
  "Data": {
    "TotalEvents": 342816,
    "TotalBytes": 36545226016,
    "TopByBytes": [
      { "TypeName": "System.String", "TotalBytes": 36545011024, "EventCount": 342814, "DominantKind": "Small" }
      // … 2 more types
    ],
    "TopBySite": [
      {
        "Frame": { "Module": "System.Private.CoreLib", "Method": "System.String.Ctor(wchar,int32)" },
        "TotalBytes": 36544797824,
        "EventCount": 342812,
        "DominantKind": "Small",
        "Identity": {
          "MethodName": "Ctor",
          "ModuleVersionId": "e8c78f6b-682f-46b5-9b01-b6df65585a7b",
          "MetadataToken": 100665517,
          "TypeFullName": "System.String"
        }
      }
      // … more sites
    ]
  }
}
```

> `TopByBytes` answers *"what is being allocated"*; `TopBySite` answers *"who is allocating it"*
> — the leaf (immediate-allocator) frame, with a MethodIdentity for handoff. The full
> caller→callee allocation tree stays behind the sampler's `query_snapshot` handle (MCP path).

### offenders report — consolidated BDN markdown

Alongside the per-method `.json` artifacts, the diagnoser writes one
`*-dotnet-diagnostics-report.md` into `BenchmarkDotNet.Artifacts/results/` — the **simplified,
human-facing** view: one row per `[DiagnosticKind]` × benchmark job, each carrying the same
`headline` as the indicator line plus a pointer to the full JSON for drill-down.

```markdown
# dotnet-diagnostics — biggest offenders

## WorkloadBenchmarks.CpuHotPath: ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3)

| kind | status | headline | artifact |
| --- | --- | --- | --- |
| cpu | ok | Captured 2035 sample(s) over 5s across 25 hotspot(s). Hottest self-cost: System.Threading.Thread.<PollGC>g__PollGCWorker\|67_0() (77.8% exclusive — 1584 self / 1584 inclusive sample(s)). | `…_CpuHotPath_ShortRun-ShortRun.2.cpu.json` |
```

> The trio is consistent: the **indicator line** (one line, in the BDN console), the **offenders
> report** (this table, one row per kind × job), and the **JSON artifact** (the full envelope for
> drill-down). `status` is `ok` or `⚠ error`; a failed capture keeps its `NotSupported` /
> `PermissionDenied` detail in the `headline`.

---

## Other kinds — canonical shapes in `tool-reference.md`

The remaining kinds are not reproduced live here (some need a domain workload; the snapshot
families are live-attach gated — see the ptrace note below). Their canonical request/response
shapes live in the tool reference:

| Kind | Reference |
|---|---|
| `event_source` (generic provider passthrough) | [`collect_events(kind="event_source")`](./tool-reference.md#collect_eventskindevent_source) |
| `activities` (ActivitySource spans)           | [`collect_events(kind="activities")`](./tool-reference.md#collect_eventskindactivities) |
| `logs` (curated ILogger view)                 | [`collect_events(kind="logs")`](./tool-reference.md#collect_eventskindlogs) |
| `jit` (tiered compilation)                    | [`collect_events(kind="jit")`](./tool-reference.md#collect_eventskindjit) |
| `db` (EF Core / SqlClient)                    | [`collect_events(kind="db")`](./tool-reference.md#collect_eventskinddb) |
| `datas` (DATAS GC tuning, Server GC)          | [`collect_events`](./tool-reference.md#collect_events) |
| `off_cpu` (where threads block)               | [`collect_sample(kind="off_cpu")`](./tool-reference.md#off-cpu-sampling-collect_samplekindoff_cpu--query_snapshot) |
| Heap walk (`inspect_heap`)                    | [`tool-reference.md`](./tool-reference.md) — live-attach gated for `source="live"` ([ptrace note](#live-attach-ptrace-snapshots--same-gate-every-surface)) |
| Thread snapshot (`collect_thread_snapshot`)   | [`tool-reference.md`](./tool-reference.md) — live-attach gated ([ptrace note](#live-attach-ptrace-snapshots--same-gate-every-surface)) |
| Process dump (`collect_process_dump`)         | [`tool-reference.md` → `collect_process_dump`](./tool-reference.md#collect_process_dump) — requires `confirm=true` |

---

## Live-attach (ptrace) snapshots — same gate, every surface

Only the **snapshot minority** of capabilities attach to the runtime via `ptrace(2)`:
`inspect_heap(source="live")`, `collect_thread_snapshot`, `collect_process_dump`, and
`capture_method_bytes`. **Every EventPipe collector above** (counters, gc, exceptions,
threadpool, contention, cpu, allocation, …) needs **no ptrace at all** — so the entire
CLI `collect` surface and the **whole BenchmarkDotNet diagnoser** are unaffected (the
in-process diagnoser is EventPipe-only — observe-only, no ptrace, no code injection).

The gate is handled identically across **MCP server and CLI** because the logic lives in
one place — `DotnetDiagnostics.Core` (`AttachGuard` + `PtraceProbe`):

- **Self-detect before you collect** — both surfaces expose the capability matrix
  (`CanAttachClrMD` + a tailored `AttachClrMdReason`):
  - MCP: `inspect_process(view="capabilities")`
  - CLI: `dotnet-diagnostics-cli capabilities [--pid <id>]` (full booleans in `--json`)
- **Tailored remediation on failure** — a denied attach returns a `PermissionDenied`
  envelope whose message is the exact fix for the *detected* environment (read live from
  `/proc/sys/kernel/yama/ptrace_scope` + the effective capability set), e.g. under the
  WSL2/Debian/Ubuntu default `ptrace_scope=1`:
  _"Grant the capability (`--cap-add SYS_PTRACE` / `cap_add: [SYS_PTRACE]` /
  `capabilities.add: ['SYS_PTRACE']`) or relax the host (`sudo sysctl -w
  kernel.yama.ptrace_scope=0`)."_
- **No-ptrace fallback** — analyze a pre-existing dump **offline, zero privilege**:
  `inspect_heap(source="dump")` (MCP) / `inspect-heap --source dump` (CLI). The shipped
  deploy manifests (compose / k8s sidecar / Fargate / Helm) already default
  `CAP_SYS_PTRACE`, so deployed sidecars never hit this gate.

> A local bare-host / WSL run under `ptrace_scope=1` is the only place the gate is felt,
> and it is a kernel boundary (Yama LSM) no userspace tool can bypass without privilege —
> the tool detects it and hands you the one-liner instead.

---

_Captured in **v0.13.0** on Linux (.NET 10.0.5). Re-capture and re-stamp this page whenever the
collector output shapes change in a release._
