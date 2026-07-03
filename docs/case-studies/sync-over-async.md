# Case study — "Requests time out under load" (and it isn't CPU)

> **The one-line lesson:** a timeout under load *feels* like "not enough CPU".
> Two tool calls prove the CPU is **idle** — and redirect the whole
> investigation to the real cause: ThreadPool starvation from a
> sync-over-async call.

This walks the full loop against
[`samples/BadCodeSample`](../../samples/BadCodeSample) endpoint
`/sync-over-async`, which reproduces a pattern that is depressingly common in
real services: an `async` API is called synchronously with
`.GetAwaiter().GetResult()`, so every in-flight request **parks a whole
ThreadPool thread** waiting on I/O.

---

## 0. The ticket

> *"Under load our endpoint starts returning 500s / client timeouts. We looked
> at the dashboards — **CPU is basically flat**, memory is fine. We think the
> box is just underpowered; can we bump the node size / add replicas?"*

That last sentence is the **wrong hypothesis**. It is also the natural one:
"slow under load + we can't see why" almost always gets filed as "need more
compute". Scaling out here would burn money and change nothing — each new
replica starves its own ThreadPool exactly the same way.

The rest of this document is how the tool suite kills that hypothesis in ~15
seconds and points at the one-line code fix.

## Reproduce the workload

Run the sample and drive concurrent load at the offending endpoint. Each request
fans out 40 blocking outbound calls, so a steady trickle is enough to starve the
pool. Pass `delaySeconds=3` so the downstream is a **deterministic loopback
`/slow-hang?seconds=3`** — this reproduces the starvation offline and every time,
independent of the public internet:

```bash
# terminal 1 — the target
ASPNETCORE_URLS=http://127.0.0.1:18190 \
  dotnet samples/BadCodeSample/bin/Release/net10.0/BadCodeSample.dll

# terminal 2 — sustained load (overlapping requests, deterministic 3s downstream)
for i in $(seq 1 50); do
  curl -s "http://127.0.0.1:18190/sync-over-async?n=40&delaySeconds=3" >/dev/null &
  sleep 0.25
done
```

> **Why the `delaySeconds` knob matters.** Omit it and `/sync-over-async` calls a
> real remote host — production-shaped, but the starvation only bites while that
> host happens to be **slow**, so it "only happens under load, in prod, sometimes".
> That flakiness is exactly why the bug is hard to catch. `delaySeconds=N` makes
> the downstream a local `/slow-hang?seconds=N`, so the blocked-thread pile-up is
> reproducible on demand — ideal for a demo or a regression test.

---

## 1. Step 0 — one triage call refutes the CPU theory

Always start with `inspect --view triage` (MCP: `inspect_process(view="triage")`).
It collects counters for a few seconds and classifies the workload.

**Baseline, idle:**

```bash
dotnet-diagnostics-cli inspect --view triage --pid <pid> --duration 6
```
```
Triage: healthy (Healthy) | top: cpu-usage=0.21%(normal), time-in-gc=0%(normal), threadpool-queue-length=0items(normal)
```

**The same call, during the load:**

```
Triage: threadpool-starvation (Critical) (also: io-bound)
  Verdict   : threadpool-starvation
  Severity  : Critical
  Indicators:
    threadpool-queue-length                  538.00 items        [critical]
    cpu-usage                                  0.11 %            [normal]     ← the tell
    time-in-gc                                 0.00 %            [normal]
    monitor-lock-contention-count              0.00 contentions  [normal]
    alloc-rate                                 0.04 MB/s         [normal]
  next:
    - collect: threadpool-queue-length=538 — collect ThreadPool events:
               collect --kind threadpool --pid <pid> --duration 10
```

**Read this carefully — this is the whole case:**

- `cpu-usage = 0.11 %`. The app is doing essentially **no CPU work** while it is
  failing requests. Adding cores / replicas cannot help something that is not
  CPU-bound. **Hypothesis "we're underpowered" is dead.**
- `threadpool-queue-length = 538` and climbing, flagged **critical**. Work is
  queued but not running.
- The classifier already names it: `threadpool-starvation`, and the `next` hint
  hands you the exact drill-down command.

We went from "buy bigger boxes" to "the ThreadPool is starved" in one call.

## 2. Step 1 — counters confirm the shape

```bash
dotnet-diagnostics-cli collect --kind counters --pid <pid> --duration 8 --json
```
```jsonc
{
  "summary": "Captured 41 counter(s) and 0 meter series over 8s — cpu-usage=0.1%, gc-heap-size=13.8.",
  "hints": [
    { "reason": "threadpool-queue-length=594 — possible ThreadPool starvation." },
    { "reason": "Low CPU + queue buildup — trace activities to see what's waiting." }
  ]
  // data.counters (trimmed to the ones that matter):
  //   cpu-usage                 = 0.07 %
  //   threadpool-queue-length   = 610
  //   threadpool-thread-count   = 636    ← the pool ballooned trying to keep up
  //   working-set               = 179 MB
}
```

`threadpool-thread-count = 636` is the second fingerprint. The ThreadPool's
hill-climbing heuristic sees the growing queue and **injects new threads, one or
two per second**, chasing a backlog it can never catch — because each new thread
is immediately parked on the same blocking call. CPU stays flat the entire time.

## 3. Step 2 — ThreadPool events name the mechanism

Follow the hint:

```bash
dotnet-diagnostics-cli collect --kind threadpool --pid <pid> --duration 10
```
```
Captured ThreadPool activity over 10s: workers latest/peak=40/40,
hill-climbing events=40, starvation reasons=36, enqueue/dequeue=0/0.
```

`starvation reasons=36` is the runtime **explicitly reporting starvation**
36 times in 10 seconds (a `ThreadPoolWorkerThreadAdjustment` with reason
`Starvation`). `hill-climbing events=40` is the pool thrashing its worker count.
This is no longer a guess — the runtime is telling us its worker threads are all
blocked.

## 4. Step 3 — see *what* the threads are blocked on

Counters tell you the pool is starved; a **thread snapshot** tells you the
exact frame every worker is stuck in. (A CPU sample would show almost nothing
here — the threads are **blocked**, not on-CPU, so the sampler has nothing to
attribute. That absence is itself confirmation the cost is *waiting*, not
*computing*.)

```bash
# thread snapshots attach via ClrMD/ptrace. On a live pid you need
# CAP_SYS_PTRACE (or ptrace_scope=0); with the CLI you can also use
# --launch to attach to a child with no extra privilege. MCP:
# collect_thread_snapshot(pid=...) then query_snapshot(view="threads-summary").
dotnet-diagnostics-cli collect --kind counters --pid <pid> \
  --capture-when 'threadCount>40' --capture thread-snapshot --window 30
```

Every parked worker shows the same tail — the smoking gun, and it points
straight at the source line:

```
Thread #42 (ThreadPool worker)  [WaitSleepJoin]
  System.Threading.ManualResetEventSlim.Wait(...)
  System.Threading.Tasks.Task.SpinThenBlockingWait(...)
  System.Runtime.CompilerServices.TaskAwaiter.GetResult()          ← blocks the thread
  BadCodeSample.Program.<>c.<Main>b__0(...)  in Program.cs:177      ← .GetAwaiter().GetResult()
```

Dozens of ThreadPool workers, all identical, all parked in
`TaskAwaiter.GetResult()`. That is sync-over-async by definition.

---

## 5. Root cause — the code

[`samples/BadCodeSample/Program.cs`](../../samples/BadCodeSample/Program.cs)
`/sync-over-async`:

```csharp
app.MapGet("/sync-over-async", (IHttpClientFactory http, HttpRequest request, int? n, int? delaySeconds) =>
{
    var clients = Math.Clamp(n ?? 20, 1, 200);
    var target = ResolveSyncOverAsyncTarget(request, delaySeconds);
    var tasks = new List<Task>();
    for (var i = 0; i < clients; i++)
    {
        tasks.Add(Task.Run(() =>                       // ← burns a pool thread…
        {
            using var client = http.CreateClient("slow");
            client.Timeout = TimeSpan.FromSeconds(5);
            try
            {
                _ = client.GetAsync(target)
                          .GetAwaiter().GetResult();    // ← …and blocks it on I/O
            }
            catch { }
        }));
    }
    Task.WaitAll(tasks.ToArray());                     // ← blocks the request thread too
    return Results.Ok(new { dispatched = clients, target });
});
```

Three separate sins, all pointing the same way:

1. `Task.Run` schedules work onto the **ThreadPool**.
2. `.GetAwaiter().GetResult()` then **blocks that pooled thread** on an async I/O
   call instead of `await`-ing it — the thread is held hostage for the whole
   downstream latency, doing nothing.
3. `Task.WaitAll(...)` blocks the incoming request's thread on top.

Under load the pool runs out of threads faster than hill-climbing can add them,
the queue backs up, and requests time out — all while the CPU sits idle.

## 6. The fix

Let async be async. `await` the calls, `Task.WhenAll` instead of `Task.WaitAll`,
and drop the `Task.Run` wrapper entirely. The sample ships exactly this as a
sibling endpoint, `/sync-over-async-fixed`, so you can A/B it live:

```diff
-app.MapGet("/sync-over-async", (IHttpClientFactory http, HttpRequest request, int? n, int? delaySeconds) =>
+app.MapGet("/sync-over-async-fixed", async (IHttpClientFactory http, HttpRequest request, int? n, int? delaySeconds) =>
 {
     var clients = Math.Clamp(n ?? 20, 1, 200);
     var target = ResolveSyncOverAsyncTarget(request, delaySeconds);
-    var tasks = new List<Task>();
-    for (var i = 0; i < clients; i++)
-    {
-        tasks.Add(Task.Run(() =>
-        {
-            using var client = http.CreateClient("slow");
-            client.Timeout = TimeSpan.FromSeconds(5);
-            try
-            {
-                _ = client.GetAsync(target).GetAwaiter().GetResult();
-            }
-            catch { }
-        }));
-    }
-    Task.WaitAll(tasks.ToArray());
+    var client = http.CreateClient("slow");
+    client.Timeout = TimeSpan.FromSeconds(5);
+    var tasks = new List<Task>();
+    for (var i = 0; i < clients; i++)
+    {
+        tasks.Add(SafeGetAsync(client, target));
+    }
+    await Task.WhenAll(tasks);
     return Results.Ok(new { dispatched = clients, target });
+
+    static async Task SafeGetAsync(HttpClient client, string url)
+    {
+        try { _ = await client.GetAsync(url); }
+        catch { }
+    }
 });
```

The awaited version holds **zero** ThreadPool threads while the downstream call
is in flight — the continuation is scheduled only when the I/O completes.

## 7. Verify the fix with the same tools

The whole point of a diagnostic suite is that verification is symmetric: re-run
the exact captures from steps 0–1 under the **same** load and confirm the
signals collapse back to the idle baseline.

```bash
# same load shape, pointed at the fixed endpoint
for i in $(seq 1 50); do
  curl -s "http://127.0.0.1:18190/sync-over-async-fixed?n=40&delaySeconds=3" >/dev/null &
  sleep 0.25
done
# …while:  dotnet-diagnostics-cli inspect --view triage --pid <pid> --duration 8
```

Real capture, fixed endpoint, **identical load** that produced `queue=538` above:

```
Triage: healthy (Healthy) | top: cpu-usage=0.13%(normal), time-in-gc=0%(normal), threadpool-queue-length=0items(normal)
  Verdict   : healthy
  Severity  : Healthy
  Indicators:
    cpu-usage                                  0.13 %            [normal]
    threadpool-queue-length                    0.00 items        [normal]
    time-in-gc                                 0.00 %            [normal]
```

Side by side, same process, same load:

| Signal | Broken (`/sync-over-async`) | Fixed (`/sync-over-async-fixed`) |
|---|---|---|
| `inspect --view triage` verdict | `threadpool-starvation` **(Critical)** | `healthy` |
| `threadpool-queue-length` | 538, climbing | **0** |
| `threadpool-thread-count` | 636 (ballooning) | flat, ~#cores |
| `cpu-usage` | 0.11 % (idle while failing) | 0.13 % (tracks real work) |
| `collect --kind threadpool` | `starvation reasons=36`, hill-climbing thrash | no starvation events |

If triage comes back `healthy` under the load that used to break it, the fix is
proven — with evidence, not vibes.

> **Gotcha when measuring both live.** Let the broken load fully drain before you
> capture the fixed run. Blocked worker threads from the broken endpoint take a
> few seconds (the downstream latency) to unwind; overlap them and the fixed
> triage will still show the broken run's tail. Restart the process, or wait it
> out, between the two captures.

---

## Takeaways (why this is the interesting case)

- **The obvious symptom lied.** "Times out under load" is not "needs more CPU".
  One `triage` call (`cpu-usage ≈ 0`) killed the expensive wrong fix before it
  was attempted.
- **Absence is a signal.** The CPU sampler showing *nothing* is not a dead end —
  for a starvation bug it is the confirmation that the cost is *waiting*.
- **The tools chain themselves.** Each step's `hints` / `next` named the exact
  follow-up command (`triage → threadpool events → thread snapshot`). You don't
  have to know the playbook by heart; the envelope walks you down it.
- **Verification is the same loop, backwards.** Re-running the "before" captures
  and watching them go quiet is how you *prove* a performance fix instead of
  hoping.

See also: [`investigation-playbooks.md` §"high latency"](../investigation-playbooks.md)
and [`bad-code-scenarios.md` #4](../bad-code-scenarios.md).
