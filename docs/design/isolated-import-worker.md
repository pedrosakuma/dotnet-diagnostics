# Isolated import worker: capability substrate

This is the internal #1050 containment substrate, **not a release gate**.
`IsolatedCaptureWorker.ProbeTrustedFixtureAsync` exercises a real child using only
an internally generated SQLite fixture, a harmless unrelated marker, and a
purpose-created same-UID helper. The [Core importer](portable-capture-import.md)
uses separate admission and rebuild processes and requires explicit worker
configuration; default import still rejects with
`UnsupportedFormat/ImportWorkerUnavailable`. The
[approved portable contract](portable-capture-contract.md) is unchanged.

The separate [SQLite structure/scalar admission operation](sqlite-structure-admission.md)
now uses this containment and supervisor for parent-owned staging databases.
The capability probe below still uses only its internally generated fixtures;
the admission operation alone does not authorize or publish an import.

## Containment and deployment boundary

The initial executable is a **single-threaded Linux x86-64 native worker**, not
a managed process with a filter applied after CLR threads already exist.
The Core test build compiles its source with `/usr/bin/cc`; no cmake, system
SQLite installation, additional NuGet dependency, or fourth public deliverable
is introduced. It loads the existing SQLitePCLRaw `libe_sqlite3.so` selected by
trusted host configuration, plus libc/libm runtime dependencies. The source is
under `src/DotnetDiagnostics.Core/Captures/Isolation/native/`.

Executable/library paths are trusted deployment configuration, never archive
fields. They and the generated fixture must exist and reject symlink paths.
The staging directory must be private (no group/other permissions). This slice
does not package a worker into the CLI/MCP distributions: that integration and
asset integrity/lifecycle remain host integration requirements.

Before SQLite opens the fixture, the child:

- Requires an empty environment; scrubs descriptors above stderr with
  `close_range`; verifies that descriptors 3 through 31 remain closed.
- Installs child-only CPU/file/core/descriptor limits, disables dumps and sets
  `no_new_privs`. It inventories its own threads and requires exactly one.
- Probes and actually installs Landlock (ABI 3 or later), allowing read-only
  access only under private staging for source admission. A separate rebuild
  profile permits regular-file writes only under its empty private output.
  Runtime libraries are loaded before
  confinement, with no external database open.
- Installs an architecture-checked, default-deny seccomp syscall allowlist.
  All process/thread creation and exec, sockets/network IPC, ptrace,
  process-VM operations, pidfds, signal access, namespace creation and
  io_uring are denied. No post-start CLR thread exception is necessary.
- Sets parent-death `SIGKILL` before confinement and verifies parent identity
  around that setting; the later filter denies changing it.

Live probes require `EACCES` for 21 denied operations, including IPv4/IPv6/Unix/
Netlink sockets, socket pairs, unrelated marker reads/writes, the helper's
`/proc/<pid>/mem`, process-VM reads/writes, ptrace, pidfd access, signal probing,
fork/vfork/clone/clone3, execve/execveat, unshare and io_uring. Only the test's
own harmless helper/marker are targeted. Both read-only and writable profiles
run these denial probes. File metadata visibility is not
claimed to be hidden by Landlock: SQLite needs path metadata calls, while
contents/writes and device/control syscalls remain restricted.

There is no in-process fallback. Missing assets, unsupported platforms or
failed kernel/native facilities return explicit `UnsupportedFormat`. Linux
tests also inject per-child `ENOSYS` for Landlock or seccomp before executing
the real worker, verifying those rejection paths without changing the host.
Windows and other architectures are unsupported until separately implemented.

## SQLite and monitoring

SQLite is configured single-threaded and its **process-wide 32 MiB hard heap
limit** is installed and allocation-tested in the child, never the long-lived
parent. The trusted fixture is opened with a correctly escaped `file:` URI
and a host-generated `immutable=1` query, read-only/private-cache flags, and
no pooling. No Microsoft.Data.Sqlite builder `immutable` flag is used.

Before application queries, the worker installs and reads back the 9 MiB
value/row, 32 KiB SQL, 64 column, expression-depth 32 and 64 variable limits;
disables attach/worker threads/triggers, enables defensive mode, disables
trusted schema and extension loading, and installs the authorizer/progress
callbacks. Fixed internal setup statements set/read back the 8 MiB cache and
zero mmap. The authorizer denies attach, DDL, unrelated PRAGMAs and extension
functions; a trusted recursive query exercises the 1,000-instruction callback
and native interrupt. The cumulative VM stop is 200,000,000 instructions.
This is capability evidence, not 200 million instructions of performance
evidence or validation of the eventual capture-schema allowlist.

The host launches the same native binary in `--monitor` mode. The monitor forks
and releases the contained worker only after it has written
`MONITOR 1 <nonce> <workerPid> <workerStartTime>`, the host has validated the
worker's `/proc/<pid>/stat` start time, durably recorded the identities and sent
`ACK\n`. `MONITOR` therefore precedes the worker's existing `READY` line, and
the `READY`/`GO`/data protocol is unchanged. An older binary that lacks monitor
support fails explicitly; there is no managed-polling fallback.

The mandatory observation window starts when the monitor releases the worker for
exec. A single-threaded native loop wakes every 1 ms with
`clock_nanosleep(CLOCK_MONOTONIC, TIMER_ABSTIME)`, checks the 10 ms gap before
any other work, probes one nonblocking stderr byte, checks exit with pidfd
`waitid`, then reads a held `/proc/<pid>/stat` descriptor for RSS and CPU. Any
gap over **10 ms**, negative gap, stderr byte, RSS/CPU/wall breach or malformed
observation kills and reaps the worker. RSS remains a sampled threshold with
possible overshoot, not a kernel hard-RSS/cgroup guarantee. `RLIMIT_AS` is not
installed yet; the address-space baseline must be measured first.

The monitor never parses archive data. Worker stderr is captured through a
nonblocking pipe; at most 256 bytes are retained and hex-encoded in the final
`MONITOR-RESULT` report. The report has one fixed, strictly parsed schema with
the nonce, outcome, exit status, sampled RSS/CPU/wall values, sample count, gap
evidence, lock status, bounded stderr evidence, monitor-thread CPU and
involuntary-context-switch delta. Unknown, duplicate, reordered, multiline or
over-1-KiB reports are rejected. `mlockall(MCL_CURRENT|MCL_FUTURE)` is
best-effort and reported as `locked=0|1`. The managed host now performs only
coarse 50 ms cancellation and wall checks; managed GC can delay cancellation
but cannot create an observation gap. A shared host can still deschedule the
native monitor for more than 10 ms; this fails closed as
`WorkerObservationGap` and is not excused.

Cancellation, overflow, timeout, malformed protocol and unsuccessful exit
cannot produce capability success. Cancellation kills and waits for the monitor;
the worker receives `SIGKILL` through its parent-death signal. Cleanup then
confirms the recorded worker identity is gone; process-tree killing is not relied
upon for isolation. Cleanup cancels and joins I/O before invoking the durable
exit callback. Failure to confirm process or I/O completion within five seconds
invalidates the operation.

### Bounded observation-gap diagnostics

`WorkerObservationGap` retains its existing exception type, code and message.
Its `CaptureStoreException.Data` contains at most 15 fixed scalar fields, created
only on failure. The managed fields now come from the native monitor report:
`WorkerGapLastValidNs`, `WorkerGapNowNs`, `WorkerGapNs`, `WorkerGapLimitNs`,
`WorkerMonitorThreadCpuDeltaNs`, `WorkerMonitorInvCtxSwDelta`,
`WorkerMonitorLocked`, `WorkerSamples`, `WorkerWallNs`, `WorkerCpuNs` and
`WorkerPeakRss`. The monitor thread CPU delta and involuntary-context-switch
delta replace the former managed GC pause and supervisor-thread fields, because
the guard no longer runs in managed code. These diagnostics indicate whether
the monitor spent CPU or was descheduled; they do not identify a specific
kernel event, alter scheduling, reset a window or excuse a gap.

The four publication authorization integration cases retain the first enriched
gap in a test-scoped first-chance exception observer, including failures later
converted into partial results. It stores one exception reference and prints
only its bounded scalar fields after the test; it does not trace successful
polls or write output on the worker thread. This observer is not production
instrumentation. A pass of this single instrumented selection is inconclusive
about earlier gaps, not evidence of a fix.

The first instrumented Linux run executed all four authorization variants and
passed 4/4, with no gap diagnostic emitted. The deterministic diagnostic/deadline
selection passed 23/23. Consequently this investigation captured no new failing
interval and cannot distinguish a physical cause for the retained earlier gaps.
It changed neither the 10 ms policy nor acceptance status; no repeat was run.

## Remaining integration

The Core pipeline now supplies archive and semantic admission, quality/origin
preservation, separate trusted-schema rebuild, per-store validator admission and
owner-bound publication. Before input it durably records the monitor, worker and
parent-I/O identities (boot ID, PID namespace, PID and process start time).
Cleanup requires confirmed
termination/quiescence; an inaccessible or reused live PID conservatively retains
storage. Persisted PIDs are never signalled. Pending parent I/O cannot be declared
dead merely because the native parser exited.

Hosts still must package/integrity-check the trusted assets and integrate their
lifecycle. No CLI/MCP packaging or additional platform support is claimed.
The capability probe accepts no externally supplied inputs; its success alone
does not authorize an import or establish complete pipeline acceptance.
