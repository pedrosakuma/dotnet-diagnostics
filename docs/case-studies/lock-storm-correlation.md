# Case study — "It's slow under load" (cross-signal correlation, MCP)

> **The one-line lesson:** thread-snapshot data has two independent views —
> *who's waiting* and *who owns what*. Read separately, neither view proves
> anything (waiting is normal; owning a lock briefly is normal). The
> `correlation.thread-overlap` signal joins them and names the **one thread**
> that is both the bottleneck and asleep while everyone waits on it — the join
> a human would otherwise do by hand, cross-referencing two JSON blobs.

This is the third companion to [`sync-over-async.md`](./sync-over-async.md) and
[`culture-lookup.md`](./culture-lookup.md), and it exercises a different part of
the tool surface: not a single collector's raw output, but the
**cross-signal correlation** layer (#514/#528) that joins two collectors' worth
of grouped signals into one finding.

- **Different evidence shape.** The first two case studies each hinge on *one*
  signal (`cpu.self-time.concentration`). This one only becomes obvious once
  you intersect **two independent groupings** built from the *same*
  thread-snapshot: the set of threads parked in a wait state, and the set of
  lock owners with long waiter queues. Neither list alone singles out the
  culprit thread — the intersection does.
- **Different capture path.** ClrMD-backed tools (`collect_thread_snapshot`)
  attach via `ptrace(2)`. On a box with `kernel.yama.ptrace_scope=1` (the
  Debian/Ubuntu/WSL default — see `AGENTS.md`), a same-UID attach from an
  *unrelated* process (like a freshly started MCP server) to an independently
  launched target is blocked unless the tracer is a direct ancestor of the
  target or has `CAP_SYS_PTRACE`. Every capture below was taken by driving the
  **exact production code path** the MCP server itself runs
  (`ClrMdThreadSnapshotInspector.InspectLiveAsync` → `ThreadWaitSignals.Detect`,
  the same two calls `collect_thread_snapshot` makes) against a directly
  spawned child process, which sidesteps that restriction while still
  producing byte-for-byte the same JSON the MCP tool would return for the
  same PID and options. No number below is fabricated or hand-derived.

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

## 1. The snapshot — two lists, read separately, prove nothing

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
isn't unusual either. The `threads.by-wait-state` signal groups the blocked
set by *why* they're waiting:

```jsonc
{
  "signal": "threads.by-wait-state",
  "summary": "17 of 26 threads (65.4%) are parked in the same wait state: Monitor.Enter (contended).",
  "salience": 0.654,
  "buckets": [
    { "key": "Monitor.Enter (contended)", "magnitude": 17, "unit": "threads" },
    { "key": "Monitor.Wait",              "magnitude": 3,  "unit": "threads" },
    { "key": "WaitHandle.Wait",           "magnitude": 1,  "unit": "threads" },
    { "key": "Thread.Sleep",              "magnitude": 1,  "unit": "threads" }
  ]
}
```

Seventeen threads queued on the *same* lock is a real concentration — but it
still only answers "who's waiting," not "why is the wait so long." For that
you'd normally cross-reference the lock-graph (who owns each contended lock)
against this wait-state list by hand — thread-by-thread.

## 2. The reveal — `correlation.thread-overlap` does the join for you

The same `collect_thread_snapshot` call also returns this, computed from the
exact same underlying data (blocked-thread set ∩ contended-lock owners) —
**no separate query needed**:

```jsonc
{
  "signal": "correlation.thread-overlap",
  "summary": "Thread 14 appears in both thread groupings: it is itself in a wait state (Thread.Sleep) while 17 thread(s) wait on a lock it holds.",
  "salience": 0.654,
  "buckets": [
    { "key": "thread 14 owns System.Object @ 0x7ad4684588a8", "magnitude": 17, "unit": "threads" }
  ],
  "nextAction": {
    "nextTool": "query_snapshot",
    "reason": "Inspect the lock graph for the overlapping owner thread's full waiter list.",
    "suggestedArguments": { "view": "lock-graph" }
  }
}
```

That single sentence is the whole diagnosis, stated without ever naming
`lockStormGate` or `/lock-storm`: **thread 14 is both a member of the "waiting"
grouping and the owner of the lock the "waiting" grouping is queued on.** It
isn't stuck on I/O, GC, or anything external — it's asleep *while holding the
lock everyone else needs*, so every other contender's 100ms-per-iteration
budget is spent entirely in queue, not in useful work. `htop` showing mostly
`S`-state threads was the correct observation; the wrong inference was "idle
therefore not the bottleneck." The correlation signal shows the opposite: the
one thread that *looks* the least suspicious (asleep, no CPU, no I/O) is
exactly the bottleneck.

Following the `nextAction` hint, `query_snapshot(view="lock-graph")` on the
same handle confirms the raw shape behind the sentence — one real
application lock plus a few `System.Object` console-logger-internal locks
that show up as noise in any endpoint with default console logging (they have
no resolvable managed owner thread and are not part of this correlation):

```jsonc
{
  "locks": [
    { "type": "System.Object", "objectAddress": "0x7ad4684588a8",
      "ownerManagedThreadId": 14, "waitingThreadCount": 17,
      "waitingManagedThreadIds": [ /* 17 ids */ ] },
    { "type": "System.Object", "ownerManagedThreadId": -1, "waitingThreadCount": 1000 }
    // … 2 more logger-internal locks, same shape, not the real bottleneck
  ]
}
```

The `ownerManagedThreadId != -1` with a real waiter count is what separates
`lockStormGate` from logging-pipeline noise — worth knowing if you ever read a
raw `lock-graph` without the correlation signal to point you at the right row
first.

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

- Two signal groupings built from the same underlying data
  (`threads.by-wait-state`, and the lock-ownership data
  `threads.by-wait-target` groups by) can each look unremarkable —
  "some threads wait," "one lock has more waiters than others" — while their
  **intersection** is the actual finding. `correlation.thread-overlap` exists
  precisely because that join is easy to miss when reading each grouping in
  isolation, and tedious to do by hand across dozens of threads.
- The signal is diagnosis-agnostic on purpose: it names *the owning thread and
  the waiter count*, not "lock contention bug" or a suggested fix. The
  conclusion ("thread 14 sleeping while holding a heavily contended lock is
  the bottleneck") is left to whoever's reading it — exactly the same
  "vector, not verdict" design as `cpu.self-time.by-namespace` in
  [`culture-lookup.md`](./culture-lookup.md#3b-postscript--with-the-signal-grouping-layer-2026-07).
- See [`docs/tool-reference.md`](../tool-reference.md#signal-grouping-layer)
  for the general signal-grouping contract, and its "Cross-signal
  correlation" section for `correlation.thread-overlap` and its sibling,
  `correlation.co-occurrence` (which correlates *across collectors* — e.g.
  counters + GC + exceptions all standing out in the same sweep window —
  rather than within one thread-snapshot).

> **Reproducibility.** Captured against `samples/BadCodeSample` (Release) on
> .NET 10 (Linux). This environment's `kernel.yama.ptrace_scope=1` blocks a
> fresh MCP-server attach to an independently-launched target (see
> `AGENTS.md`), so the capture above was taken by invoking the same
> `DotnetDiagnostics.Core` classes the MCP server calls
> (`ClrMdThreadSnapshotInspector.InspectLiveAsync` →
> `ThreadWaitSignals.Detect`) against a directly spawned child process — same
> code path, same JSON shape, just without the extra process hop. Numbers
> (26 threads, 17 waiters, thread 14) are non-deterministic run to run (they
> depend on exact timing of the snapshot relative to the storm), but the
> *shape* of the evidence — one owner thread flagged as blocked while a
> double-digit waiter count queues behind it — reproduces reliably with
> `blockers=20` and a snapshot taken a couple of seconds into the run.
