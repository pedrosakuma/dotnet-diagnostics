# Case study — "It's slow under load" (bounded thread/lock drilldown, MCP)

> **The one-line lesson:** thread-snapshot data has two independent views —
> *who's waiting* and *who owns what*. Read separately, neither view proves
> anything (waiting is normal; owning a lock briefly is normal). The
> bounded lock pages provide a stable owner thread id, and an exact stack query
> shows that the **same thread** is asleep while everyone waits on it.

This is the third companion to [`sync-over-async.md`](./sync-over-async.md) and
[`culture-lookup.md`](./culture-lookup.md), and it exercises a different part of
the tool surface: not a single collector's raw output, but the
bounded summary/detail projections plus exact selectors over one retained
thread-snapshot handle.

- **Different evidence shape.** The first two case studies each hinge on *one*
  signal (`cpu.self-time.concentration`). This one only becomes obvious once
  you join **two independent views** of the *same* thread-snapshot: the ranked
  locks with waiter counts and the exact stack of a selected lock owner.
- **Different capture path.** ClrMD-backed tools (`collect_thread_snapshot`)
  attach via `ptrace(2)`. On a box with `kernel.yama.ptrace_scope=1` (the
  Debian/Ubuntu/WSL default — see `AGENTS.md`), a same-UID attach from an
  *unrelated* process (like a freshly started MCP server) to an independently
  launched target is blocked unless the tracer is a direct ancestor of the
  target or has `CAP_SYS_PTRACE`. Every capture below was taken by driving the
  same ClrMD inspector used by the MCP server against a directly spawned child
  process, which sidesteps that restriction. The current MCP path registers
  the complete artifact and returns bounded projections; it does not eagerly
  build capture-sized signal indexes. No number below is fabricated.

---

## 0. The ticket

> *"Under moderate load, `/lock-storm`-style endpoints get slow and some
> requests seem to serialize completely. `htop` shows most workers idle or in
> `S` state, not spinning — so it doesn't look CPU-bound. We suspect GC, but
> the heap is small. What's actually stalling?"*

The tempting wrong answer: *"Threads in `S` state aren't the problem — CPU
isn't pegged, so it must be I/O or GC somewhere else."* A thread-snapshot
proves otherwise: the workers aren't idle, they're **queued behind a single
lock**, and the thread holding that lock is itself asleep.

## Reproduce the workload

```bash
# terminal 1 — the target
ASPNETCORE_URLS=http://127.0.0.1:5512 \
  dotnet samples/BadCodeSample/bin/Release/net10.0/BadCodeSample.dll

# terminal 2 — many contenders serialize through one lock, each holding it for 100ms
curl -s "http://127.0.0.1:5512/lock-storm?seconds=20&blockers=20" >/dev/null
```

`/lock-storm` (`samples/BadCodeSample/Program.cs`, endpoint 15) spins up
`blockers` tasks that each loop: `lock (lockStormGate) { Interlocked.Increment(...); Thread.Sleep(100); }`.
The `Thread.Sleep` **inside** the lock is the whole point of this scenario —
it means whichever task currently owns the lock is, at that exact moment,
*also* classified as "likely blocked" by the thread-snapshot heuristics (a
`Thread.Sleep` frame at the top of its stack). One thread is simultaneously
the answer to "who's waiting?" (no — it's running) and "who does everyone else
wait on?" (yes). That's the overlap.

---

## 1. The snapshot — a bounded first page, not a capture-sized index

`collect_thread_snapshot(processId=<pid>)` taken ~2s into the storm:

```jsonc
{
  "threads": 26, "locks": 4,
  "blocked": 22,          // threads with IsLikelyBlocked == true
  "contendedLocks": 4     // locks with >1 waiter
}
```

22 of 26 threads look "blocked." Four locks are contended. Read in isolation,
this says nothing: some waiting is normal under load, and a lock with waiters
isn't unusual either. `query_snapshot(view="top-blocked", offset=0)` returns
only the first eight ranked candidates and a continuation:

```jsonc
{
  "view": "top-blocked",
  "totalThreads": 26,
  "candidateThreads": 22,
  "threadOffset": 0,
  "nextThreadCursor": "<opaque>",
  "threads": [
    { "managedThreadId": 21, "waitReason": "Monitor.Enter (contended)" }
    // ... seven more bounded rows, each with at most eight frames
  ]
}
```

The handle retains every captured thread and frame, but the response remains
bounded. Continue with `nextThreadCursor`, or move directly to the ranked lock
graph when the collection hint reports contended locks.

## 2. The reveal — stable lock identity leads to the exact owner stack

`query_snapshot(view="lock-graph", offset=0)` ranks the most-contended locks
first. Selecting the demonstrated lock by address returns a bounded waiter-id
page without losing its stable owner identity:

```jsonc
{
  "view": "lock-graph",
  "waiterOffset": 0,
  "nextWaiterCursor": "<opaque>",
  "locks": [{
    "objectAddress": "0x7ad4684588a8",
    "ownerManagedThreadId": 14,
    "waitingThreadCount": 17,
    "waitingManagedThreadIds": [ /* first 8 ids */ ],
    "totalWaitingManagedThreadIds": 17,
    "omittedWaitingManagedThreadIds": 9
  }]
}
```

The exact selector
`query_snapshot(view="stack", threadId=14)` then shows `Thread.Sleep` at the
top of that owner's stack. The owner id is the join key: thread 14 is asleep
*while holding the lock everyone else needs*, so every contender's
100ms-per-iteration budget is spent in the queue rather than useful work.
`htop` showing mostly `S`-state threads was correct; the wrong inference was
"idle therefore not the bottleneck."

The first ranked `lock-graph` page on the same handle also shows the surrounding
shape — one real
application lock plus a few `System.Object` console-logger-internal locks
that show up as noise in any endpoint with default console logging (they have
no resolvable managed owner thread and are not part of this correlation):

```jsonc
{
  "locks": [
    { "type": "System.Object", "objectAddress": "0x7ad4684588a8",
      "ownerManagedThreadId": 14, "waitingThreadCount": 17,
      "waitingManagedThreadIds": [ /* first 8 ids */ ] },
    { "type": "System.Object", "ownerManagedThreadId": -1, "waitingThreadCount": 1000 }
    // … 2 more logger-internal locks, same shape, not the real bottleneck
  ]
}
```

The `ownerManagedThreadId != -1` with a real waiter count is what separates
`lockStormGate` from logging-pipeline noise — worth knowing if you ever read a
raw `lock-graph`; contention-first ranking puts that row before low-value noise.

## 3. Root cause and the fix

`lockStormGate` in `Program.cs` is a single `object` shared by every
contender, and the critical section holds it for the *entire* 100ms
`Thread.Sleep` — meaning the lock is held ~100ms per iteration per thread,
serializing all `blockers` tasks through one gate with no batching, sharding,
or reduced hold time. The fix depends on what `lockStormGate` actually
protects in a real system (this sample intentionally leaves it as a bare
demo), but the tools already tell you *where* to look and *why* it's slow —
not "add more instances," but "this one lock's hold time, held by whichever
thread currently owns it, is the entire critical path."

## Takeaways

- Bounded pages can look unremarkable independently — "some threads wait,"
  "one lock has more waiters than others" — while the stable owner id makes
  their **intersection** explicit without capture-sized response payloads.
- `nextThreadCursor`, `nextLockCursor`, and `nextWaiterCursor` make omitted
  evidence discoverable. Exact `address` and `threadId` selectors avoid
  replaying earlier pages when the decisive identity is already known.
- See [`docs/tool-reference.md`](../tool-reference.md) for the bounded
  thread-snapshot query contract.

> **Reproducibility.** Captured against `samples/BadCodeSample` (Release) on
> .NET 10 (Linux). This environment's `kernel.yama.ptrace_scope=1` blocks a
> fresh MCP-server attach to an independently-launched target (see
> `AGENTS.md`), so the capture above was taken by invoking the same
> `DotnetDiagnostics.Core` inspector the MCP server calls
> (`ClrMdThreadSnapshotInspector.InspectLiveAsync`) against a directly spawned
> child process. Numbers
> (26 threads, 17 waiters, thread 14) are non-deterministic run to run (they
> depend on exact timing of the snapshot relative to the storm), but the
> *shape* of the evidence — one owner thread flagged as blocked while a
> double-digit waiter count queues behind it — reproduces reliably with
> `blockers=20` and a snapshot taken a couple of seconds into the run.
