# Isolated import worker: capability substrate

This is an internal #1050 prerequisite, **not an importer or release gate**.
`IsolatedCaptureWorker.ProbeTrustedFixtureAsync` exercises a real child using only
an internally generated SQLite fixture, a harmless unrelated marker, and a
purpose-created same-UID helper. Public `PortableCaptureUseCases.ImportAsync`
still rejects with `UnsupportedFormat/ImportWorkerUnavailable`. No archive
admission, schema admission/rebuild, package-3 provenance, or publication is
implemented here. The [approved portable contract](portable-capture-contract.md)
and existing export APIs are unchanged.

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
asset integrity/lifecycle remain necessary before enabling import.

Before SQLite opens the fixture, the child:

- Requires an empty environment; scrubs descriptors above stderr with
  `close_range`; verifies that descriptors 3 through 31 remain closed.
- Installs child-only CPU/file/core/descriptor limits, disables dumps and sets
  `no_new_privs`. It inventories its own threads and requires exactly one.
- Probes and actually installs Landlock (ABI 3 or later), allowing read-only
  access only under private staging. Runtime libraries are loaded before
  confinement, with no external database open.
- Installs an architecture-checked, default-deny seccomp syscall allowlist.
  All process/thread creation and exec, sockets/network IPC, ptrace,
  process-VM operations, pidfds, signal access, namespace creation and
  io_uring are denied. No post-start CLR thread exception is necessary.

Live probes require `EACCES` for 21 denied operations, including IPv4/IPv6/Unix/
Netlink sockets, socket pairs, unrelated marker reads/writes, the helper's
`/proc/<pid>/mem`, process-VM reads/writes, ptrace, pidfd access, signal probing,
fork/vfork/clone/clone3, execve/execveat, unshare and io_uring. Only the test's
own harmless helper/marker are targeted. File metadata visibility is not
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
process-tree killing is not relied upon for isolation.

Deadline regression tests use explicit elapsed times to cover the wall-budget
boundary with and without mandatory observations. A real stalled child may hit
either the wall deadline or the observation-gap guard first; both must reject
capabilities. The live test does not assume scheduler delivery within 10 ms.

## Remaining integration

Untrusted archive/header admission, full SQLite schema/data validation and
rebuild, bounded row/JSON frames, destination publication/remapping and
per-store cross-process validator admission remain separate work. Future
hosts must package the actual trusted worker and enforce the whole import
lifecycle/resource contract. The current launcher accepts no externally
supplied archives or SQLite inputs and does not authorize import.
