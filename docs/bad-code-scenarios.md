# Bad-code scenarios — exercising dotnet-diagnostics-mcp end-to-end

`samples/BadCodeSample/` is a minimal API where every endpoint triggers a
different, well-known .NET anti-pattern. Use it to validate that the MCP
server (and an LLM driving it) can pinpoint each problem using nothing but the
current unified tool surface.

## Topology used for these scenarios

We run **three** containers in the same Docker network, with the sample +
sidecar sharing a PID namespace and `/tmp` (mirroring the K8s sidecar):

```bash
docker network create diagmcp-net 2>/dev/null || true
docker volume  create badcode-tmp >/dev/null

docker run -d --name badcode --network diagmcp-net \
  -v badcode-tmp:/tmp \
  -p 18180:8080 \
  badcode-sample:dev

docker run -d --name badcode-mcp --network diagmcp-net \
  --pid=container:badcode \
  -v badcode-tmp:/tmp \
  --user 0 \
  --cap-add SYS_PTRACE \
  -e MCP_BEARER_TOKEN=dev-token \
  -p 18887:8080 \
  dotnet-diagnostics-mcp:dev
```

> `--cap-add SYS_PTRACE` is required for live memory readers
> (`collect_thread_snapshot`, `inspect_heap(source="live")`, `capture_method_bytes`,
> `get_bytes(kind="module")`, and
> `collect_sample(kind="cpu", resolveMethodInstantiations=true)`). Without it,
> these tools return a structured `PermissionDenied` error on hosts where
> `kernel.yama.ptrace_scope=1` (Debian/Ubuntu/WSL default). EventPipe-only
> tools work without it. See [`docs/local-docker-sidecar.md`](./local-docker-sidecar.md)
> for the full mitigation matrix.

Trigger a scenario from your shell (the `badcode` container exposes the API on
`http://127.0.0.1:18180`) **at the same time** as you collect from the MCP
sidecar (`http://127.0.0.1:18887`).

The sample PID inside the shared namespace is `1` (it owns the container).

## The scenarios

Each row lists: the symptom the user/LLM would report, the endpoint that
reproduces it, the MCP tools that would identify it, and what to look for in
the output.

| # | Symptom | Trigger | Primary tool(s) | Expected signal |
|---|---|---|---|---|
| 1 | "CPU pegged at 100% on one core" | `GET /cpu-burn?ms=3000` | `collect_events(kind="counters")`, `collect_sample(kind="cpu")` | `cpu-usage` near 100% during the burn; top sampled frames in `System.Security.Cryptography.SHA256` |
| 2 | "Memory keeps growing" | repeated `GET /leak?mb=4` | `collect_events(kind="counters")` over time, `collect_events(kind="gc")`, `collect_process_dump` | `gc-heap-size` and `working-set` climb monotonically; gen-2 collections increase; dump shows large `byte[]` retained by `leakedBuffers` |
| 3 | "First-chance exception storms in logs" | `GET /exceptions?count=2000` | `collect_events(kind="counters")`, `collect_events(kind="exceptions")` | `exception-count` rate jumps; collector returns 100% `FormatException` ("Input string was not in a correct format") |
| 4 | "Requests time out under load even though CPU is low" | `GET /sync-over-async?n=40` | `collect_events(kind="counters")`, `collect_sample(kind="cpu")` | `threadpool-queue-length` grows, `threadpool-thread-count` climbs slowly; CPU low; sampled stacks show `GetAwaiter().GetResult` / `Task.Wait` frames |
| 5 | "Throughput drops as concurrency grows" | `GET /lock-contention?threads=64&ms=4000` | `collect_events(kind="counters")`, `collect_sample(kind="cpu")` | `monitor-lock-contention-count` jumps to thousands/sec; stacks dominated by `Monitor.Enter` / `SpinWait` |
| 6 | "GC pauses are frequent in production" | repeated `GET /loh-alloc?count=200` | `collect_events(kind="counters")`, `collect_events(kind="gc")` | `loh-size` and `gen2-gc-count` rise; collector reports gen-2 collections with `LowMemory` / `Induced` reasons (or just frequent gen-2) |
| 7 | "Outbound HTTP calls are slow" | `GET /slow-http?url=https://httpbin.org/delay/3` | `collect_events(kind="event_source")` `name=System.Net.Http`, `collect_events(kind="counters")` with `System.Net.Http` | EventSource emits `Request*/Response*` events with latency between them; `requests-started-rate` and `current-requests` visible in counters |
| 8 | "The process dies from an unhandled exception" | `GET /crash?mode=unhandled` | `collect_events(kind="crash-guard")` started before the request | `unhandledExceptionObserved=true`; `finalException` has the fatal type/message and `query_snapshot(view="stack")` returns the managed stack when available |
| 9 | "A trivial in-memory lookup pegs the CPU at ~95%" | sustained `GET /culture-lookup?iterations=3000000` | `inspect_process(view="triage")`, `collect_sample(kind="cpu")` → `query_snapshot(view="call-tree")` | triage observes critical CPU utilization and emits `cpu.compute-demand` with high confidence; the call-tree drill then lands on a high-*exclusive* `System.Globalization.CompareInfo.IcuGetHashCodeOfString` leaf (~89% of CPU) — a culture-aware `Dictionary` comparer |
| 10 | "It's slow under load, but threads look idle, not spinning" | `GET /lock-storm?seconds=20&blockers=20` | `collect_thread_snapshot` | `threads.by-wait-state` signal shows most threads parked on `Monitor.Enter (contended)`; `correlation.thread-overlap` signal names the one owner thread that is itself asleep (`Thread.Sleep`) while 15+ others queue on the lock it holds |

`/crash?mode=stackoverflow` is intentionally abrupt and may terminate before
all EventPipe buffers flush. `/crash?mode=oom` simulates an OOM-class fatal
termination with an unhandled exception rather than forcing the host into real
memory exhaustion.

> **Deterministic repro + a fixed variant (scenario 4).** `/sync-over-async`
> accepts `delaySeconds=N`, which points its fan-out at a local
> `/slow-hang?seconds=N` instead of a flaky public host — so the ThreadPool
> starvation reproduces offline, every time. `/sync-over-async-fixed` is the
> awaited, non-blocking version with the same parameters, for a live before/after.
> The full narrated investigation is in
> [`case-studies/sync-over-async.md`](./case-studies/sync-over-async.md).

> **Runtime-only bug + a fixed variant (scenario 9).** `/culture-lookup` builds a
> `Dictionary` with `StringComparer.InvariantCultureIgnoreCase`, so every
> `TryGetValue` pays a culture-aware ICU string hash — a cost that is **invisible in
> the endpoint source** (the hot loop is identical to the fast version).
> `/culture-lookup-fixed` uses `OrdinalIgnoreCase` (~12× faster) for a live
> before/after. This is the scenario that shows what the tools add *over* static
> analysis; the full MCP-driven investigation is in
> [`case-studies/culture-lookup.md`](./case-studies/culture-lookup.md).

> **Cross-signal correlation (scenario 10).** `/lock-storm` holds
> `lockStormGate` across a `Thread.Sleep(100)`, so the current lock owner is
> simultaneously "likely blocked" and the reason everyone else is queued. Read
> separately, "some threads wait" and "one lock has more waiters than others"
> are both unremarkable; the `correlation.thread-overlap` signal joins the two
> groupings and names the one owner thread that is asleep while holding the
> lock. The full narrated investigation, including the raw lock-graph and the
> wait-state signal it complements, is in
> [`case-studies/lock-storm-correlation.md`](./case-studies/lock-storm-correlation.md).

## How an LLM should drive this

A useful system message for the LLM (already encoded in
`investigation-playbooks.md`) is:

1. **Discover**: `inspect_process(view="list")` → pick the target PID
2. **Probe**: `inspect_process(view="info")` + `inspect_process(view="capabilities")` so the LLM
   knows whether stack sampling is available (CoreCLR vs NativeAOT)
3. **Cheap signal**: `collect_events(kind="counters")` with `System.Runtime` for 5–10s to
   classify the symptom (CPU? memory? exceptions? threads?)
4. **Targeted capture** based on what the counters showed:
   - CPU high → `collect_sample(kind="cpu")`
   - Memory growing → `collect_events(kind="gc")`, then `collect_process_dump` if a
     dump is justified
   - Exception count spiking → `collect_events(kind="exceptions")`
   - Process crash / unhandled exception → start `collect_events(kind="crash-guard")` before reproducing
   - Latency on outbound HTTP → `collect_events(kind="event_source")` `System.Net.Http`
5. **Report**: aggregate the captured artifacts into a root cause + fix.

## What this proves

If a model — even without seeing the source — can:

- explain *why* `/cpu-burn` is hot (SHA256 in a tight loop),
- spot the leaking list behind `/leak`,
- name `FormatException` as the dominant exception type,
- point at the sync-over-async pattern from sampled stacks,
- correlate `monitor-lock-contention-count` with the shared `lock(...)` block,

…then the MCP surface is good enough for an end-to-end diagnostic loop. That
is the target benchmark for this project; track which scenarios any given
model fails so we can decide whether to add more tools (e.g. lock-contention
EventPipe specifics, allocation sampling, thread stack dump beyond CPU).
