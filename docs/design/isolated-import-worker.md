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

A bounded nonce/version handshake precedes the parent's `GO` frame. The
parent starts mandatory observation **before** sending `GO`; startup is also
wall-time bounded. Defaults remain 60 seconds CPU, 120 seconds wall and
256 MiB RSS; internal operator limits can only decrease them. The kernel CPU
rlimit supplements parent CPU sampling. Handshake stdout is capped at 256
bytes, remaining stdout and stderr at 4 KiB each, with bounded lookahead.

The parent measures actual inter-observation gaps. Any mandatory-window gap
over **10 ms** invalidates the result and terminates the child; it is not
recorded as successful monitoring. This uses ordinary scheduling, not timer
resolution/priority changes or a real-time guarantee. RSS is a sampled stop
threshold with possible overshoot, **not** a kernel hard-RSS/cgroup guarantee.
Cancellation, overflow, timeout, malformed protocol and unsuccessful exit
cannot produce capability success. Cleanup kills and waits for the child;
process-tree killing is not relied upon for isolation. Positively confirmed exit
within the last valid sample's deadline ends the RSS window, allowing bounded
parent-only evidence finalization. It does not extend wall or cancellation limits.
Cleanup cancels and joins I/O before invoking the durable exit callback. Failure
to confirm I/O completion within five seconds invalidates the operation.

Deadline regression tests use explicit elapsed times to cover the wall-budget
boundary with and without mandatory observations. A real stalled child may hit
either the wall deadline or the observation-gap guard first; both must reject
capabilities. The live test does not assume scheduler delivery within 10 ms.

### Bounded observation-gap diagnostics

`WorkerObservationGap` retains its existing exception type, code and message.
Its `CaptureStoreException.Data` contains at most 12 fixed scalar fields, created
only on failure. All times are elapsed monotonic `TimeSpan` ticks (100 ns units)
from that worker supervisor's stopwatch, not UTC or raw hardware counter ticks.

- `WorkerLastValidSampleTicks`, `WorkerCurrentTicks`, `WorkerGapTicks` and
  `WorkerGapLimitTicks` contain the exact inputs to the failed guard; the limit
  remains 100,000 ticks. A rejected sample never advances the last valid sample.
- `WorkerProtocolPhase` identifies handshake, initial observation, sending,
  input closure, receiving, stderr drain, exit wait or final checks.
  `WorkerPollStage` identifies the most recent poll step or the completion-gap
  check. Neither field claims a native process state.
- Optional `WorkerPollStartedTicks` and
  `WorkerLastCompletedPollDurationTicks` describe the latest poll start and
  last fully completed poll duration. On a failure inside a poll, the duration
  belongs to the preceding completed poll, not the failing one.
- Optional `WorkerMetricsStartedTicks` and `WorkerMetricsFinishedTicks` bound
  the latest refresh/RSS/CPU-read window through the existing sample timestamp.
  Missing completion means no completed positive-RSS sample timestamp was
  recorded for that window; it is not a zero-duration read. Endpoint differences
  include any scheduling delay within the window, not just native syscall cost.
- Optional `WorkerSenderStatus` and `WorkerReceiverStatus` snapshot managed task
  status while constructing the exception. They are not native exit evidence,
  input-consumption acknowledgments, or task completion timestamps.

Tracking retains only the latest scalar timings/task references; it does not
keep an event history, read additional process metrics on failure, or record
paths, payloads or process IDs. Added clock reads have finite overhead and may
perturb timing; they do not redefine the valid-sample timestamp, reset a window,
alter scheduling or excuse a gap. The diagnostic fields alone cannot attribute
an interval to OS scheduling, GC or a specific syscall.

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
owner-bound publication. Before input it durably records native and parent-I/O
identities (boot ID, PID namespace and PID). Cleanup requires confirmed
termination/quiescence; an inaccessible or reused live PID conservatively retains
storage. Persisted PIDs are never signalled. Pending parent I/O cannot be declared
dead merely because the native parser exited.

Hosts still must package/integrity-check the trusted assets and integrate their
lifecycle. No CLI/MCP packaging or additional platform support is claimed.
The capability probe accepts no externally supplied inputs; its success alone
does not authorize an import or establish complete pipeline acceptance.
