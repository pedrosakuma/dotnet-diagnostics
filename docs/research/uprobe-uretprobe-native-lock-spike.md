# Paired uprobe/uretprobe latency spike for native mutex blocking

Issue [#852](https://github.com/pedrosakuma/dotnet-diagnostics/issues/852) asks
whether pairing a `uprobe` on `pthread_mutex_lock` entry with a `uretprobe` on
its return could measure call latency directly and promote sampled mutex-call
activity to *confirmed blocking* — a stronger claim than the sampled-entry
"activity" evidence the collector already reports, and potentially cheaper to
obtain than a full off-CPU/futex correlation.

**Conclusion: defer.** The kernel mechanism itself is a reasonable fit
(uretprobes track return instances per-thread, so single-thread recursion
naturally resolves via a LIFO stack), but *duration measured this way is not,
by itself, sufficient evidence of blocking*, and several structural gaps
(process-exit/thread-exit races, `pthread_cancel` at deferred cancellation
points, and confirmation that a "long" `pthread_mutex_lock` call actually
contains a futex wait rather than a slow but non-blocking fast path such as a
priority-inheritance robust-mutex retry loop) make it strictly weaker than the
existing off-CPU + futex correlation path for the specific claim this
taxonomy cares about ("did this thread block on contention"), while adding a
second, higher-overhead, higher-privilege collection mode. This sandbox could
not perform a live measurement (see below) and no public benchmark
establishes a reliable duration-alone threshold for confirmed blocking. Per
the issue's own acceptance criteria — "do not change the default evidence
classification without evidence that paired returns are reliable" — that
evidence does not exist yet. This spike recommends **not** building the
opt-in prototype now and instead filing a narrower follow-up (below) if a
representative bare-metal Linux host becomes available for calibration.

## What the collector already does today

`PerfNativeLockContentionSampler`
(`src/DotnetDiagnostics.Core/NativeLockContention/PerfNativeLockContentionSampler.cs`)
already uses `perf probe -x <libc> <event>=pthread_mutex_lock` — a plain
entry uprobe, no return probe — throttled with `-c <samplePeriod>` so only
1-in-N hits record a DWARF call-graph sample. Its own doc comment and the
first collector note it emits are explicit that "counts are sampled
mutex-call hits… not measured wait time" and that a fast-path uncontended
lock is indistinguishable from a blocked one at this uprobe. That is the
"activity" evidence level in
`NativeContentionEvidence`/`NativeContentionEvidenceLevels`
(`src/DotnetDiagnostics.Core/NativeLockContention/NativeContentionEvidence.cs`):
`none < activity < probable-blocking < confirmed-blocking`.

The stronger levels come from a completely different mechanism:
`OffCpuAggregator`/`NativeLockContentionUx`
(`src/DotnetDiagnostics.Core/OffCpu/`,
`src/DotnetDiagnostics.Core/NativeLockContention/NativeLockContentionUx.cs`)
correlate `perf sched` off-CPU spans against native-sync frame markers
(`pthread_mutex`, `futex`, `lll_lock`, …). A span promotes to
`confirmed-blocking` only when it has at least one **closed** (non-censored)
native-sync off-CPU span, zero censored spans, zero ambiguous native-sync
frame spans, and no probable-non-futex native-sync evidence in the window
(`NativeLockContentionUx.cs:296-303`); any censored/open span, ambiguous
frame, or non-futex native-sync signal demotes the whole result to
`probable-blocking`, and the summary text explicitly says a censored span's
duration is "a lower bound" and "not confirmed blocking"
(`NativeLockContentionUx.cs:482-484`). This is the taxonomy AGENTS.md
describes: off-CPU + futex correlation is the only path that can claim
"confirmed", and only for spans that actually close within the window and
carry no disqualifying ambiguity.

A paired uprobe/uretprobe mode would be a **third** mechanism sitting between
these two: like the existing sampler, it targets the *call*, not the futex
wait; unlike the existing sampler, it measures the call's wall-clock
duration directly instead of only recording hit counts.

## Kernel/perf mechanism (documented, not measured live — see next section)

- **Uprobes and uretprobes are both supported via `perf probe`.** The
  `p[:[GRP/][EVENT]] PATH:OFFSET` form sets a uprobe; `r[:[GRP/][EVENT]]
  PATH:OFFSET` (or `PATH:OFFSET%return`) sets a uretprobe
  ([kernel uprobetracer docs](https://docs.kernel.org/trace/uprobetracer.html)).
  `perf probe -x <obj> <fn>` for entry and `perf probe -x <obj>
  <fn>%return` for return is the documented pairing
  ([perf-probe(1)](https://www.man7.org/linux/man-pages/man1/perf-probe.1.html),
  [Red Hat: Creating uprobes with perf](https://docs.redhat.com/en/documentation/red_hat_enterprise_linux/8/html/monitoring_and_managing_system_status_and_performance/creating-uprobes-with-perf_monitoring-and-managing-system-status-and-performance)).
  `CONFIG_UPROBE_EVENTS=y` is required (checked and present in this sandbox's
  kernel config, see below).
- **Return-instance tracking is per-task, not a fixed pool.** Unlike kernel
  `kretprobe`, which pre-allocates a fixed `maxactive` pool of
  `kretprobe_instance` structures and increments `nmissed` once that pool is
  exhausted under concurrency, uretprobes hijack the return address on the
  calling thread's own stack and push a `return_instance` onto a per-task
  list ([uretprobes implementation, LWN #543924](https://lwn.net/Articles/543924/);
  `kernel/events/uprobes.c`, `prepare_uretprobe`/`return_instance`). That
  means same-thread recursion/reentrancy (a thread re-locking a
  recursive-type `pthread_mutex_t`, or one lock call nested inside another
  distinct mutex's critical section) resolves correctly via ordinary LIFO
  matching — there is no `maxactive`-style silent drop from a shared pool
  the way there is for kernel kretprobes. Cross-thread symmetric unlock
  (thread B unlocking a mutex thread A locked) is a non-issue for
  correlation purposes: `pthread_mutex_unlock` by a different thread is
  undefined behavior for non-robust/non-error-checking mutex types and, when
  it does happen, is simply a second, independent entry/return pair keyed on
  its own (tid, address) — it does not corrupt thread A's still-pending
  `pthread_mutex_lock` pair.
- **What *does* break correlation:** the kernel documentation itself warns
  that if a return-probed function's caller never actually executes the
  hijacked return address, the event is lost — this happens on (a) **process
  or thread exit** while inside the probed call (the return_instance is
  simply discarded when the task tears down — a structurally unmatched,
  i.e. censored, entry, mirroring exactly the "censored/open span" case the
  off-CPU aggregator already models), (b) **`pthread_cancel`** at a
  deferred cancellation point inside glibc's futex wait loop, which unwinds
  through the call via `longjmp`-style stack unwinding rather than a normal
  `ret`, and (c) signal-handler reentrancy hitting the same probe while
  already inside a probed region on the same thread, which the kernel
  documentation and several kretprobe/uretprobe write-ups flag as a known
  edge case for losing or misattributing the *matching* return event (not
  usually corrupting an unrelated thread's data, since tracking is
  per-task).
- **Symbol/version resolution matches the existing collector's approach and
  needs no new work.** glibc 2.34 merged `libpthread.so` into `libc.so.6`
  ([Red Hat: Why glibc 2.34 removed libpthread](https://developers.redhat.com/articles/2021/12/17/why-glibc-234-removed-libpthread));
  `ProcMapsLibcResolver` already resolves the live libc mapping from
  `/proc/<pid>/maps` rather than hard-coding a path, so PIE/ASLR base-address
  resolution and the libpthread-vs-libc split are already handled identically
  for an entry-only or a paired entry/return probe — this is not new spike
  surface.
- **Overhead is per-call, not per-sample, and higher for a return probe
  than a bare counter.** Public microbenchmarks (Brendan Gregg's BPF
  Performance Tools materials; the uprobe overhead discussion in
  [gitlab-com/gl-infra/observability#1383](https://gitlab.com/gitlab-com/gl-infra/observability/team/-/issues/1383))
  put a bare uprobe counter around ~1 µs/event and a paired
  entry+return latency measurement (BPF `funclatency`-style) around ~2.5
  µs/event, versus ~3.5 µs/event for a DWARF call-graph sample. Crucially,
  this collector's existing entry-only design deliberately **throttles**
  callchain capture to 1-in-`samplePeriod` hits precisely because "every
  mutex call still traps even though only 1-in-N callchains are recorded"
  (see the sampler's own note text). A paired entry+return latency
  measurement cannot use that throttle the same way: to compute a duration
  you need *both* the entry and the matching return for the *same* call,
  so you cannot cheaply "sample 1-in-N calls" without adding a per-thread
  in-kernel filter (not available via bare `perf probe`) — the naïve
  approach traps on every single call on both ends, i.e. full 1x
  overhead on a hot mutex, not 1/N-reduced overhead. On a genuinely
  mutex-hot workload (the exact scenario this taxonomy exists to diagnose)
  that is the overhead profile most likely to perturb the very contention
  being measured.

## What this sandbox could and could not verify

Per AGENTS.md's WSL2-perf-quirks note, this environment is unlikely to be
representative of a native Linux host or a Kubernetes sidecar, and that
turned out to be true here:

```
$ uname -r
6.18.33.2-microsoft-standard-WSL2
$ perf --version
WARNING: perf not found for kernel 6.18.33.2-microsoft
  (needs linux-tools-6.18.33.2-microsoft-standard-WSL2, the version-suffixed wrapper doesn't match)
$ zcat /proc/config.gz | grep UPROBE
CONFIG_ARCH_SUPPORTS_UPROBES=y
CONFIG_UPROBES=y
CONFIG_UPROBE_EVENTS=y
$ cat /proc/sys/kernel/perf_event_paranoid
2
$ cat /proc/sys/kernel/yama/ptrace_scope
1
$ id
uid=1000(pedrotravi) groups=...,1001(docker)
$ sudo -n true; echo $?
1   # no passwordless sudo available
$ /usr/lib/linux-tools/6.8.0-137-generic/perf probe -x /lib/x86_64-linux-gnu/libc.so.6 'test=pthread_mutex_lock'
No permission to write tracefs.
Please run this command again with sudo.
  Error: Failed to add events.
```

The running kernel does have `CONFIG_UPROBE_EVENTS=y` compiled in, so the
mechanism is available in principle. But:

- The distro `perf` wrapper cannot even resolve a matching version for this
  WSL2 kernel release (the same quirk AGENTS.md already documents for
  off-CPU sampling); the version-pinned binary under
  `/usr/lib/linux-tools/<kernel>` had to be invoked directly.
- `/sys/kernel/debug/tracing` (and therefore `uprobe_events`) is mode `0700`
  root-only, and this account has no `sudo` access in this sandbox — so
  neither creating nor listing a uprobe was possible, despite the process's
  capability bounding set nominally including `cap_sys_admin`. That mismatch
  (capabilities present in the bounding set but the operation still denied)
  is itself informative: it demonstrates that bounding-set membership alone
  is not sufficient to predict success, and any production capability-gate
  for this mode must probe the actual tracefs write, not just inspect
  `/proc/self/status`.
- No live measurement — event volume, `nmissed` counts, or actual paired
  latency numbers — could therefore be collected in this sandbox. All
  overhead and correlation-behavior figures above are drawn from kernel
  documentation and public benchmarks, not reproduced here. This is exactly
  the outcome the issue anticipated ("likely a container without
  CAP_SYS_ADMIN or perf infra"), and no fabricated numbers are reported in
  its place.

This also directly demonstrates the capability-degradation path the
existing collector already needs, and a new mode would need to reuse: when
`perf probe` fails with a tracefs-permission error, `PerfNativeLockContentionSampler`
already surfaces a structured `InvalidOperationException` naming the cause
("perf probe could not create a uprobe… lacks CAP_SYS_ADMIN / tracefs write
access") rather than crashing or silently returning empty data. Any
uretprobe-based mode must degrade the same way — falling back to the
existing entry-only sampler or the off-CPU path, never to a hard failure
of `collect_sample`.

## Correlation-key design (paper design, not implemented)

If this were built, the entry/return pairing key would be:

**`(tid, mutex_address)` with a per-key LIFO stack, scoped to the run.**

- `tid` comes from the `perf script`/trace record's own thread field (same
  source the existing off-CPU and CPU samplers already use).
- `mutex_address` is the probed function's first argument (`%di`/`x0` per
  the SysV/AArch64 calling convention) — fetched via perf's `FETCHARGS`
  syntax (`%di` on x86-64) at the entry probe. It requires no change to the
  return probe (which only needs `$retval`, if used at all — this taxonomy
  does not need the return code).
- On entry, push a `(seq, timestamp)` tuple onto the `(tid, address)`
  stack. On return, pop **the top of that same thread+address stack**
  (LIFO), pair it with the return's timestamp, and emit a closed-pair
  event with duration = return_ts − entry_ts.
- **Recursion / reentrancy (same thread re-locking a recursive mutex):**
  handled for free by the LIFO stack — nested lock/unlock on the same
  `(tid, address)` naturally matches innermost-first, mirroring what the
  kernel's own per-task `return_instance` list already guarantees.
- **Cross-thread unlock:** irrelevant to this key, because the key is
  scoped per-thread on the *lock* call only; an unlock happening on a
  different thread never touches thread A's still-open entry.
  `pthread_mutex_unlock` would get its own independent `(tid, address)`
  keyspace if probed at all (and per the existing collector's own note,
  unlock is already best-effort/non-mandatory).
- **Cancellation:** an entry pushed onto the stack with no matching return
  before the thread exits (via `pthread_cancel` unwinding through a
  cancellation point inside the lock call, or normal thread/process exit)
  is left on the stack at end-of-window. It must be reported as an explicit
  **unmatched/censored entry**, exactly mirroring the existing off-CPU
  aggregator's censored-span concept — never silently dropped and never
  promoted to a duration.
- **Process exit:** any entries still on any stack when the recording
  process/perf session ends are unmatched by construction and reported the
  same way.
- **Bounded retention:** per
  [`docs/resource-boundedness.md`](../resource-boundedness.md) convention,
  the per-`(tid, address)` stack must have a hard cap on outstanding
  entries (e.g. a small constant, since realistic nesting depth for
  distinct mutexes on one thread is tiny) with an explicit
  drop-oldest-and-count-it note if exceeded, and a hard cap on the number
  of distinct `(tid, address)` keys tracked concurrently (mirroring the
  existing `PerfDataMaxBytes`/`PerfScriptSampleBudget` caps in
  `PerfNativeLockContentionSampler`), reporting eviction counts rather than
  growing unbounded on a mutex-hot multi-thread workload.

## Is duration alone sufficient to promote to confirmed blocking?

**No — recommend NOT promoting.** A long `pthread_mutex_lock` call is
consistent with several outcomes: (a) the thread genuinely blocked on a
futex wait (the case this taxonomy wants to confirm), (b) glibc's
adaptive/spinning mutex implementation busy-spun on-CPU for a bounded
number of iterations before falling back to a futex wait, so *duration
includes on-CPU spin time that off-CPU accounting would never have counted
as blocking in the first place*, or (c) a slow non-blocking path inside
glibc itself (e.g. priority-inheritance or robust-mutex bookkeeping,
`PTHREAD_MUTEX_ROBUST` dead-owner recovery) that never reaches a futex
syscall at all. None of these are distinguishable from call duration alone
without also observing whether a `futex(2)` syscall actually occurred
during the call — which is precisely what the existing off-CPU/syscall
correlation path already does more directly, by looking at the scheduler
trace and syscall breakdown rather than inferring it from wall-clock time.
A duration-based mode would therefore, at best, reproduce
`probable-blocking` evidence (a "this call was slow" signal, corroborating
but not confirming), and doing so requires calibrating a
workload/hardware-dependent latency threshold this spike has no
representative data to set. Per the issue's own acceptance criteria, the
default evidence classification (`NativeContentionEvidenceLevels`) must not
change without evidence paired returns are reliable, and this spike does
not produce that evidence.

## Overhead and diagnostic value vs. the existing off-CPU path

| | Off-CPU + futex correlation (existing) | Paired uprobe/uretprobe (spiked) |
|---|---|---|
| Confirms actual blocking | Yes, for closed spans (`ClosedNativeSyncSpanCount`) | No — duration alone conflates spin time and non-blocking slow paths |
| Overhead shape | Scheduler tracepoints, off-CPU only (no per-call trap on the fast/uncontended path) | Traps on **every** mutex call (entry + return), including fast-path uncontended locks |
| Privilege | Already required by `collect_sample(kind="off_cpu")` | Same tracefs/`CAP_SYS_ADMIN` requirement as today's entry-only mode, no worse — but pays it on every call, not 1-in-N |
| Degradation on missing capability | Existing structured error path | Would reuse the same path |
| New failure modes | None beyond today's censored-span handling | Adds process/thread-exit and `pthread_cancel` unmatched-pair cases (mitigable via the censored-entry design above, but new surface) |
| Verified in this sandbox | No live measurement (WSL2/no CAP_SYS_ADMIN) | No live measurement (same constraint) |

The paired mode does not clearly beat the existing path on the metric that
matters (confirmed vs. probable), while costing strictly more overhead on
the hot path it is meant to diagnose.

## Recommendation

- **Do not change `NativeContentionEvidenceLevels` or its promotion rules.**
  No evidence gathered here shows paired uprobe/uretprobe duration reliably
  distinguishes confirmed blocking from spin time or non-blocking slow
  paths.
- **Do not build the opt-in prototype now.** The correlation-key design
  above is sound on paper and the kernel mechanism (per-task
  `return_instance` list) handles same-thread recursion cleanly, but (a)
  this sandbox cannot validate the design against real event volume,
  `nmissed`/lost-event rates, or actual latency distributions on a
  representative Linux host, (b) the overhead profile is worse than the
  existing entry-only sampler's throttled design precisely on mutex-hot
  workloads, and (c) even a working implementation would only add
  `probable-blocking`-strength corroboration that the existing off-CPU path
  already provides more directly.
- **Suggested follow-up issue (not opened as part of this spike):** if a
  bare-metal (or otherwise representative, root/`CAP_SYS_ADMIN`-capable)
  Linux host becomes available, re-run this spike as a live calibration:
  measure real `nmissed`/lost-pair rates on a synthetic mutex-contention
  workload, and specifically test whether call duration correlates with an
  observed `futex(2)` syscall in the same window (via `perf trace`/`strace`
  on the same PID) — that correlation, if strong and reproducible, would be
  the actual missing evidence needed to reconsider evidence-level
  promotion. Absent that data, keep the existing off-CPU path as the only
  source of `confirmed-blocking`.

No code changes were made as a result of this spike; the existing
entry-only `PerfNativeLockContentionSampler` and the off-CPU evidence
taxonomy are unchanged.
